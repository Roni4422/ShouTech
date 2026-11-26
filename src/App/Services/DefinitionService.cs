using System.Globalization;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Generic definition-set service plus the dedicated installment, loyalty and
/// delivery workflows (ribbon completion F1). All invariants are validated
/// here before the repository is touched; the repository re-checks
/// uniqueness, status transitions and balances inside its transactions as a
/// second line of defence. No fake saves: every method persists for real.
/// </summary>
public sealed class DefinitionService
{
    private readonly IDefinitionRepository _repository;

    public DefinitionService(IDefinitionRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    internal IDefinitionRepository Repository => _repository;

    // ------------------------------------------------------------ generic sets

    public static DefSet ResolveSet(string setKey) => DefSets.Get(setKey);

    public Task<IReadOnlyList<DefRow>> ListAsync(string setKey, string companyId, CancellationToken cancellationToken = default)
        => _repository.ListAsync(ResolveSet(setKey), RequireCompany(companyId), cancellationToken);

    public async Task<string> SaveAsync(string setKey, string companyId, string? id, IReadOnlyDictionary<string, object?> values, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(values);
        var set = ResolveSet(setKey);
        var normalized = NormalizeValues(set, values);
        return await _repository.SaveAsync(set, companyId, id, normalized, actor, cancellationToken).ConfigureAwait(false);
    }

    public Task SoftDeleteAsync(string setKey, string companyId, string id, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireActor(actor);
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Row id is required.", nameof(id));
        return _repository.SoftDeleteAsync(ResolveSet(setKey), companyId, id, actor, cancellationToken);
    }

    public Task SetActiveAsync(string setKey, string companyId, string id, bool isActive, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireActor(actor);
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Row id is required.", nameof(id));
        return _repository.SetActiveAsync(ResolveSet(setKey), companyId, id, isActive, actor, cancellationToken);
    }

    /// <summary>
    /// Validates a raw editor value dictionary against the set descriptor:
    /// unknown columns are rejected, required columns must be non-blank,
    /// numeric/integer/date values must parse, allowed-value lists are
    /// enforced, and known range rules (percentages, amounts, weekday) hold.
    /// Returns a normalized copy (trimmed strings, parsed numbers/dates).
    /// </summary>
    public static IReadOnlyDictionary<string, object?> NormalizeValues(DefSet set, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(values);
        var known = new HashSet<string>(set.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase) { "is_active" };
        foreach (var key in values.Keys)
            if (!known.Contains(key))
                throw new InvalidOperationException($"Column '{key}' is not part of definition set '{set.Key}'.");

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in set.Columns)
        {
            values.TryGetValue(column.Name, out var raw);
            result[column.Name] = NormalizeValue(set, column, raw);
        }
        if (values.TryGetValue("is_active", out var active))
            result["is_active"] = ToBool(active);
        return result;
    }

    private static object? NormalizeValue(DefSet set, DefColumn column, object? raw)
    {
        var text = raw switch
        {
            null => null,
            string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture)
        };
        if (text is null)
        {
            if (column.Required)
                throw new InvalidOperationException($"Field '{column.Name}' is required in set '{set.Key}'.");
            // Bool columns are switches backed by NOT NULL columns: never null.
            // Columns flagged HasDefault are NOT NULL with a schema default:
            // an unset value normalizes to the type zero instead of null.
            return column.Type switch
            {
                DefColumnType.Bool => false,
                DefColumnType.Number when column.HasDefault => 0m,
                DefColumnType.Integer when column.HasDefault => 0L,
                DefColumnType.Text when column.HasDefault => string.Empty,
                _ => null
            };
        }
        switch (column.Type)
        {
            case DefColumnType.Text:
                if (column.AllowedValues is { Count: > 0 } && !column.AllowedValues.Contains(text, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Value '{text}' is not allowed for '{column.Name}' in set '{set.Key}'. Allowed: {string.Join(", ", column.AllowedValues)}.");
                return text;
            case DefColumnType.Number:
            {
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) &&
                    !decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out number))
                    throw new InvalidOperationException($"Field '{column.Name}' must be a number.");
                ValidateRange(column.Name, number);
                return number;
            }
            case DefColumnType.Integer:
            {
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    throw new InvalidOperationException($"Field '{column.Name}' must be a whole number.");
                ValidateRange(column.Name, integer);
                return integer;
            }
            case DefColumnType.Bool:
                return ToBool(raw);
            case DefColumnType.Date:
            {
                var candidate = text.Length >= 10 ? text[..10] : text;
                if (!DateOnly.TryParse(candidate, CultureInfo.InvariantCulture, out var date))
                    throw new InvalidOperationException($"Field '{column.Name}' must be a date (yyyy-MM-dd).");
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            default:
                return text;
        }
    }

    private static void ValidateRange(string columnName, decimal value)
    {
        switch (columnName)
        {
            case "discount_pct":
            case "commission_pct":
                if (value < 0 || value > 100)
                    throw new InvalidOperationException($"Field '{columnName}' must be between 0 and 100.");
                break;
            case "target_amount":
            case "price":
            case "min_qty":
            case "min_order":
            case "pos_sort_order":
                if (columnName != "pos_sort_order" && value < 0)
                    throw new InvalidOperationException($"Field '{columnName}' cannot be negative.");
                break;
            case "weekday":
                if (value < 0 || value > 6)
                    throw new InvalidOperationException("Field 'weekday' must be between 0 (Sunday) and 6 (Saturday).");
                break;
            case "visit_order":
            case "sort_order":
            case "material_id":
                if (value < 0)
                    throw new InvalidOperationException($"Field '{columnName}' cannot be negative.");
                break;
        }
    }

    private static bool ToBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        string s when bool.TryParse(s, out var parsed) => parsed,
        string s => s == "1",
        _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
    };

    // ------------------------------------------------------------ installments

    /// <summary>
    /// Builds the schedule for a plan: the financed amount (total - down
    /// payment) is split into equal monthly installments rounded to cents,
    /// with the rounding remainder carried by the LAST row, so the schedule
    /// always sums exactly to the financed amount. Due dates advance monthly
    /// from the start date.
    /// </summary>
    public static IReadOnlyList<InstallmentScheduleRow> BuildSchedule(decimal totalAmount, decimal downPayment, int installmentCount, DateOnly startDate)
    {
        if (totalAmount <= 0)
            throw new InvalidOperationException("Installment total must be positive.");
        if (downPayment < 0 || downPayment >= totalAmount)
            throw new InvalidOperationException("Down payment must be at least 0 and less than the total amount.");
        if (installmentCount <= 0)
            throw new InvalidOperationException("Installment count must be positive.");
        var financed = totalAmount - downPayment;
        var baseAmount = Math.Round(financed / installmentCount, 2, MidpointRounding.AwayFromZero);
        var rows = new List<InstallmentScheduleRow>(installmentCount);
        for (var i = 0; i < installmentCount; i++)
        {
            var amount = i == installmentCount - 1
                ? financed - baseAmount * (installmentCount - 1)
                : baseAmount;
            rows.Add(new InstallmentScheduleRow
            {
                Id = Guid.NewGuid().ToString(),
                Seq = i + 1,
                DueDate = startDate.AddMonths(i),
                Amount = amount,
                PaidAmount = 0m,
                Status = ScheduleStatuses.Due
            });
        }
        return rows;
    }

    public async Task<string> CreateInstallmentAsync(string companyId, InstallmentPlan plan, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.PartyCode))
            throw new InvalidOperationException("Party code is required for an installment plan.");
        var schedule = BuildSchedule(plan.TotalAmount, plan.DownPayment, plan.InstallmentCount, plan.StartDate);
        plan.PartyCode = plan.PartyCode.Trim();
        if (string.IsNullOrWhiteSpace(plan.Id))
            plan.Id = Guid.NewGuid().ToString();
        if (!InstallmentStatuses.All.Contains(plan.Status))
            plan.Status = InstallmentStatuses.Active;
        return await _repository.CreateInstallmentAsync(companyId, plan, schedule, actor, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<InstallmentPlan>> ListInstallmentsAsync(string companyId, string? partyCode = null, string? status = null, CancellationToken cancellationToken = default)
        => _repository.ListInstallmentsAsync(RequireCompany(companyId), partyCode, status, cancellationToken);

    public Task<IReadOnlyList<InstallmentScheduleRow>> ListScheduleAsync(string companyId, string installmentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installmentId))
            throw new ArgumentException("Installment plan is required.", nameof(installmentId));
        return _repository.ListScheduleAsync(RequireCompany(companyId), installmentId, cancellationToken);
    }

    public Task DeleteInstallmentAsync(string companyId, string installmentId, string actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (string.IsNullOrWhiteSpace(installmentId))
            throw new ArgumentException("Installment plan is required.", nameof(installmentId));
        return _repository.SoftDeleteInstallmentAsync(RequireCompany(companyId), installmentId, actor, cancellationToken);
    }

    /// <summary>Collects against one schedule row; the repository posts the party_ledger credit in the same transaction.</summary>
    public Task<InstallmentScheduleRow> CollectInstallmentAsync(string companyId, string scheduleId, decimal amount, DateOnly paidDate, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireActor(actor);
        if (string.IsNullOrWhiteSpace(scheduleId))
            throw new ArgumentException("Schedule row is required.", nameof(scheduleId));
        if (amount <= 0)
            throw new InvalidOperationException("Collection amount must be positive.");
        return _repository.CollectInstallmentAsync(companyId, scheduleId, amount, paidDate, actor, cancellationToken);
    }

    // ----------------------------------------------------------------- loyalty

    public Task<IReadOnlyList<LoyaltyAccount>> ListLoyaltyAccountsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.ListLoyaltyAccountsAsync(RequireCompany(companyId), cancellationToken);

    public Task<IReadOnlyList<LoyaltyEntry>> ListLoyaltyEntriesAsync(string companyId, string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("Loyalty account is required.", nameof(accountId));
        return _repository.ListLoyaltyEntriesAsync(RequireCompany(companyId), accountId, cancellationToken);
    }

    public Task<decimal> EarnPointsAsync(string companyId, string partyCode, decimal points, string? docRef, string? notes, string actor, CancellationToken cancellationToken = default)
    {
        if (points <= 0)
            throw new InvalidOperationException("Earned points must be positive.");
        RequireActor(actor);
        return _repository.AdjustLoyaltyAsync(RequireCompany(companyId), partyCode, points, docRef, notes, actor, cancellationToken);
    }

    /// <summary>Redeems points; the repository rejects the redemption inside its transaction when the balance is insufficient.</summary>
    public Task<decimal> RedeemPointsAsync(string companyId, string partyCode, decimal points, string? docRef, string? notes, string actor, CancellationToken cancellationToken = default)
    {
        if (points <= 0)
            throw new InvalidOperationException("Redeemed points must be positive.");
        RequireActor(actor);
        return _repository.AdjustLoyaltyAsync(RequireCompany(companyId), partyCode, -points, docRef, notes, actor, cancellationToken);
    }

    // ---------------------------------------------------------------- delivery

    public Task<IReadOnlyList<DeliveryRequest>> ListDeliveryRequestsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.ListDeliveryRequestsAsync(RequireCompany(companyId), cancellationToken);

    public async Task<string> SaveDeliveryRequestAsync(string companyId, DeliveryRequest request, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount < 0)
            throw new InvalidOperationException("Delivery request amount cannot be negative.");
        if (request.DocNo < 0)
            throw new InvalidOperationException("Delivery request number cannot be negative.");
        if (!DeliveryStatuses.All.Contains(request.Status))
            throw new InvalidOperationException($"Unknown delivery status '{request.Status}'.");
        return await _repository.SaveDeliveryRequestAsync(companyId, request, actor, cancellationToken).ConfigureAwait(false);
    }

    public Task SetDeliveryStatusAsync(string companyId, string requestId, string newStatus, string? driverCode, string actor, CancellationToken cancellationToken = default, string? receivedBy = null, byte[]? signatureImage = null)
    {
        RequireCompany(companyId);
        RequireActor(actor);
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("Delivery request is required.", nameof(requestId));
        if (!DeliveryStatuses.All.Contains(newStatus))
            throw new InvalidOperationException($"Unknown delivery status '{newStatus}'.");
        return _repository.SetDeliveryStatusAsync(companyId, requestId, newStatus, driverCode, actor, cancellationToken, receivedBy, signatureImage);
    }

    /// <summary>
    /// Proof-of-delivery completion (sales cycle W4): the Delivered
    /// transition REQUIRES the receiver's name; the repository stamps
    /// delivered_at automatically and stores the optional signature.
    /// </summary>
    public Task DeliverWithProofAsync(string companyId, string requestId, string receivedBy, byte[]? signatureImage, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireActor(actor);
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("Delivery request is required.", nameof(requestId));
        if (string.IsNullOrWhiteSpace(receivedBy))
            throw new InvalidOperationException("The name of the person who received the delivery is required (proof of delivery).");
        return _repository.SetDeliveryStatusAsync(
            companyId, requestId, DeliveryStatuses.Delivered, null, actor, cancellationToken,
            receivedBy.Trim(), signatureImage);
    }

    public Task DeleteDeliveryRequestAsync(string companyId, string requestId, string actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("Delivery request is required.", nameof(requestId));
        return _repository.SoftDeleteDeliveryRequestAsync(RequireCompany(companyId), requestId, actor, cancellationToken);
    }

    public Task<IReadOnlyList<DriverSettlement>> ListDriverSettlementsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.ListDriverSettlementsAsync(RequireCompany(companyId), cancellationToken);

    public Task<string> CloseDriverCashAsync(string companyId, string driverCode, DateOnly settleDate, decimal collectedAmount, decimal feesAmount, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireActor(actor);
        if (string.IsNullOrWhiteSpace(driverCode))
            throw new InvalidOperationException("Driver code is required to close driver cash.");
        if (collectedAmount < 0)
            throw new InvalidOperationException("Collected amount cannot be negative.");
        if (feesAmount < 0)
            throw new InvalidOperationException("Fees cannot be negative.");
        if (feesAmount > collectedAmount)
            throw new InvalidOperationException("Fees cannot exceed the collected amount.");
        return _repository.CloseDriverCashAsync(companyId, driverCode.Trim(), settleDate, collectedAmount, feesAmount, actor, cancellationToken);
    }

    // ------------------------------------------------------------------ guards

    private static string RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
        return companyId;
    }

    private static string RequireActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new InvalidOperationException("A current user is required for audited writes.");
        return actor;
    }
}
