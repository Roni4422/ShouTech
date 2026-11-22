namespace ShouTech.Domain.Entities;

public sealed record PurchaseMatchEvaluationSnapshot(
    Guid EvaluationId,
    long InvoiceDocId,
    int EvaluationVersion,
    int PolicyVersion,
    string Decision,
    IReadOnlyList<string> ReasonCodes,
    string PolicySnapshotJson,
    string ResultSnapshotJson,
    string? EvaluatedBy,
    DateTime EvaluatedAt);
