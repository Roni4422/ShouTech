namespace ShouTech.Domain.Entities;

public sealed record PurchaseLandedCostChargeDraft(
    string ChargeCode,
    string Description,
    decimal Amount,
    string AllocationBasis,
    IReadOnlyDictionary<long, decimal>? ManualAmounts = null,
    string? ExpenseAccountCode = null);

public sealed record PurchaseLandedCostSummary(
    Guid Id,
    Guid CompanyId,
    string DocumentNumber,
    DateOnly DocumentDate,
    long ReceiptDocumentId,
    string ReceiptDocumentNumber,
    long? SupplierId,
    string? SupplierName,
    string CurrencyCode,
    decimal ExchangeRate,
    decimal TotalAmount,
    decimal TotalBaseAmount,
    string Status,
    Guid? JournalEntryId,
    Guid? ReversalId);
