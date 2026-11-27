namespace ShouTech.Domain.Entities;

/// <summary>Per-charge credit modes for landed-cost vouchers (migration 108).
/// A charge is either Unlinked (<see cref="Payable"/>: the voucher creates
/// the liability to its payee) or <see cref="Linked"/> (the liability
/// already exists on a posted supplier document; posting reclasses the
/// cost out of that document's original debit account and creates NO new
/// liability) — never both.</summary>
public static class LandedCostCreditModes
{
    /// <summary>Unlinked: the charge creates a payable — GL credit to AP
    /// control (2201) plus a party-ledger credit to the charge payee.</summary>
    public const string Payable = "Payable";

    /// <summary>Linked: the charge links to a posted supplier document that
    /// already carries the liability. Posting credits the account that
    /// document originally debited (resolved from its posted journal at
    /// draft time) and writes NO party-ledger entry, so no duplicate
    /// payable can arise.</summary>
    public const string Linked = "Linked";

    /// <summary>Legacy interim-108 mode, superseded by <see cref="Linked"/>.
    /// No longer creatable; retained only so drafts written by the interim
    /// build remain postable.</summary>
    public const string AlreadyBooked = "AlreadyBooked";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[] { Payable, Linked }, StringComparer.OrdinalIgnoreCase);
}

public sealed record PurchaseLandedCostChargeDraft(
    string ChargeCode,
    string Description,
    decimal Amount,
    string AllocationBasis,
    IReadOnlyDictionary<long, decimal>? ManualAmounts = null,
    string? ExpenseAccountCode = null,
    /// <summary>Payee party (parties.ledger_id) owed this charge; null = the
    /// (primary) receipt supplier. For Linked charges the payee is taken
    /// from the linked document's supplier.</summary>
    long? PayeePartyId = null,
    string CreditMode = LandedCostCreditModes.Payable,
    /// <summary>Posted supplier document (purchase_documents.doc_id) that
    /// already carries this charge's liability. Required for Linked
    /// charges; must be null for Payable charges.</summary>
    long? LinkedDocumentId = null);

/// <summary>One receipt contributing lines to a landed-cost voucher
/// (migration 108, L4). A null <see cref="IncludedLineIds"/> includes every
/// eligible stock-valued line of the receipt; otherwise only the listed
/// receipt line ids are allocated.</summary>
public sealed record PurchaseLandedCostReceiptSelection(
    long ReceiptDocumentId,
    IReadOnlyList<long>? IncludedLineIds = null);

/// <summary>A posted purchase receipt offered by the landed-cost receipt picker.</summary>
public sealed record PurchaseLandedCostReceiptOption(
    long ReceiptDocumentId,
    string ReceiptDocumentNumber,
    DateOnly ReceiptDate,
    long? SupplierId,
    string? SupplierName,
    string CurrencyCode,
    decimal ExchangeRate);

/// <summary>One eligible (stock-valued) receipt line offered for inclusion
/// in a landed-cost voucher.</summary>
public sealed record PurchaseLandedCostReceiptLineOption(
    long ReceiptDocumentId,
    string ReceiptDocumentNumber,
    long ReceiptLineId,
    int LineNo,
    long MaterialId,
    string? MaterialCode,
    string Description,
    decimal BaseQuantity,
    decimal BaseGoodsAmount);

/// <summary>A company party offered by the landed-cost payee picker
/// (PartyId is parties.ledger_id, the party-ledger key).</summary>
public sealed record PurchaseLandedCostPayeeOption(
    long PartyId,
    string? Code,
    string Name,
    string Kind);

/// <summary>A posted supplier document offered by the landed-cost link
/// picker: a document whose liability a Linked charge may consume.
/// Documents already consumed by a posted landed-cost voucher are not
/// offered.</summary>
public sealed record PurchaseLandedCostLinkableDocumentOption(
    long DocumentId,
    string DocumentNumber,
    DateOnly DocumentDate,
    long SupplierId,
    string? SupplierName,
    decimal GrandTotal);

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

// ---- merged from src/Domain/Entities/PurchaseDocumentAttachment.cs ----
/// <summary>
/// Attachment owner discriminators (migration 098): the 086 attachment
/// store is generalized, not duplicated — one table/service serves
/// purchase documents (legacy default), sales documents and parties.
/// </summary>
public static class AttachmentOwnerKind
{
    public const string PurchaseDocument = "PURDOC";
    public const string SalesDocument = "SALDOC";
    public const string Party = "PARTY";
}

/// <summary>
/// Attachment metadata. <see cref="DocumentId"/> is the numeric owner id
/// for document owners (0 for parties); <see cref="OwnerKind"/> /
/// <see cref="OwnerRef"/> carry the generalized owner identity
/// (owner_ref = document id as text, or the party id for parties).
/// </summary>
public sealed record PurchaseDocumentAttachment(
    Guid Id, Guid CompanyId, long DocumentId, string StorageKey, string OriginalName,
    string MediaType, long ByteLength, string Sha256, string UploadedBy, DateTime UploadedAt,
    string OwnerKind = AttachmentOwnerKind.PurchaseDocument, string? OwnerRef = null);
