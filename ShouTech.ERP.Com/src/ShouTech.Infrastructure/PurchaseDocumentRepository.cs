using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral persistence for purchase documents and their chain
/// links (SQLite and PostgreSQL). The buying twin of
/// CommercialDocumentRepository: same table shapes, same document_links
/// table, same side-effect contracts:
/// - RCV (purchase receipt): one stock movement (QtyIn) per stockable line
///   valued by the ONE StockValuationEngine, in base-unit quantities; a
///   receipt of a quality_check_required material is diverted to the
///   company's quarantine warehouse (decision Q2) with a Draft incoming
///   inspection created in the same transaction, exactly like the
///   legacy StkPost receipt path.
/// - PUR (purchase invoice): a party_ledger credit for the supplier
///   grand total and a balanced journal entry (Dr Purchases [+ input VAT]
///   / Cr Payable) posted in the same transaction.
/// - PRT (purchase return): the mirror of RCV + PUR — one QtyOut
///   movement per stockable line, a party_ledger debit for the supplier,
///   and a balanced journal entry (Dr Payable / Cr Purchase Returns [+ Cr
///   input VAT]) in the same transaction.
/// - RFQ / PO: no side effects.
/// </summary>
public sealed class PurchaseDocumentRepository : IPurchaseDocumentRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public PurchaseDocumentRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    public async Task<CommercialDocumentSnapshot?> FindPurchaseByIdAsync(long docId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        long foundId;
        Guid companyId;
        string docKind;
        string docNo;
        DateOnly docDate;
        long? partyId;
        string? partyName;
        long? warehouseId;
        string currencyCode;
        decimal exchangeRate;
        decimal subtotal;
        decimal discountAmount;
        decimal taxAmount;
        decimal grandTotal;
        decimal paidAmount;
        string status;
        string? notes;
        string? partyCode;
        string? branchName;
        string? salesRepName;
        string? costCenter;
        string? warehouseName;
        DateOnly? deliveryDate;
        DateOnly? validTill;

        await using (var header = connection.CreateCommand())
        {
            header.CommandText = """
                SELECT doc_id, company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id,
                       currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total,
                       paid_amount, status, notes, party_code, branch_name, sales_rep_name, cost_center,
                       warehouse_name, delivery_date, valid_till
                FROM purchase_documents
                WHERE doc_id = @docId AND is_deleted = 0
                """;
            Add(header, "@docId", docId);
            await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            foundId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (!Guid.TryParse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture), out companyId) || companyId == Guid.Empty)
                throw new InvalidOperationException("The saved purchase document has an invalid company identifier.");
            docKind = reader.GetString(2);
            docNo = reader.GetString(3);
            docDate = DateOnly.Parse(Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            partyId = reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
            partyName = reader.IsDBNull(6) ? null : reader.GetString(6);
            warehouseId = reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture);
            currencyCode = reader.GetString(8);
            exchangeRate = Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture);
            subtotal = Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture);
            discountAmount = Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture);
            taxAmount = Convert.ToDecimal(reader.GetValue(12), CultureInfo.InvariantCulture);
            grandTotal = Convert.ToDecimal(reader.GetValue(13), CultureInfo.InvariantCulture);
            paidAmount = Convert.ToDecimal(reader.GetValue(14), CultureInfo.InvariantCulture);
            status = reader.GetString(15);
            notes = reader.IsDBNull(16) ? null : reader.GetString(16);
            partyCode = reader.IsDBNull(17) ? null : reader.GetString(17);
            branchName = reader.IsDBNull(18) ? null : reader.GetString(18);
            salesRepName = reader.IsDBNull(19) ? null : reader.GetString(19);
            costCenter = reader.IsDBNull(20) ? null : reader.GetString(20);
            warehouseName = reader.IsDBNull(21) ? null : reader.GetString(21);
            deliveryDate = reader.IsDBNull(22) ? null : DateOnly.Parse(Convert.ToString(reader.GetValue(22), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            validTill = reader.IsDBNull(23) ? null : DateOnly.Parse(Convert.ToString(reader.GetValue(23), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
        }

        var lines = new List<CommercialLineSnapshot>();
        await using (var lineCommand = connection.CreateCommand())
        {
            lineCommand.CommandText = """
                SELECT line_id, line_no, material_id, material_code, description, unit, qty, unit_price,
                       discount_pct, tax_rate, line_subtotal, line_tax, line_total, warehouse_id
                FROM purchase_document_lines
                WHERE doc_id = @docId
                ORDER BY line_no
                """;
            Add(lineCommand, "@docId", docId);
            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lines.Add(new CommercialLineSnapshot(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(12), CultureInfo.InvariantCulture),
                    reader.IsDBNull(13) ? null : Convert.ToInt64(reader.GetValue(13), CultureInfo.InvariantCulture)));
            }
        }

        return new CommercialDocumentSnapshot(
            foundId, companyId, docKind, docNo, docDate, partyId, partyName, warehouseId,
            currencyCode, exchangeRate, subtotal, discountAmount, taxAmount, grandTotal,
            paidAmount, status, notes, lines.AsReadOnly(),
            partyCode, branchName, salesRepName, costCenter, warehouseName, deliveryDate, validTill);
    }

    public async Task<long> CreatePurchaseDraftAsync(CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.CompanyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.DocKind)) throw new ArgumentException("Document kind is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.DocNo)) throw new ArgumentException("Document number is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        if (draft.Lines.Count == 0) throw new InvalidOperationException("A commercial document requires at least one line.");

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var docDateText = draft.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            decimal subtotal = 0m, taxAmount = 0m, grandTotal = 0m;
            var computed = new List<(decimal Subtotal, decimal Tax, decimal Total)>();
            foreach (var line in draft.Lines)
            {
                if (line.Qty <= 0m) throw new InvalidOperationException("Document line quantities must be greater than zero.");
                var lineSubtotal = decimal.Round(line.Qty * line.UnitPrice * (1m - line.DiscountPct / 100m), 2);
                var lineTax = decimal.Round(lineSubtotal * line.TaxRate / 100m, 2);
                var lineTotal = lineSubtotal + lineTax;
                computed.Add((lineSubtotal, lineTax, lineTotal));
                subtotal += lineSubtotal;
                taxAmount += lineTax;
                grandTotal += lineTotal;
            }

            long docId;
            var insertHeader = CreateCommand(connection, transaction,
                "INSERT INTO purchase_documents (company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id, currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total, status, notes, created_by, party_code, delivery_date, valid_till, cost_center, branch_name, sales_rep_name, warehouse_name) " +
                "VALUES (@company, @kind, @no, @date, @party, @partyName, @warehouse, @currency, @rate, @subtotal, @discount, @tax, @total, @status, @notes, @actor, @partyCode, @deliveryDate, @validTill, @costCenter, @branchName, @salesRepName, @warehouseName)" +
                (_provider == "PostgreSQL" ? " RETURNING doc_id" : "; SELECT last_insert_rowid();"));
            Add(insertHeader, "@company", draft.CompanyId.ToString("D"));
            Add(insertHeader, "@kind", draft.DocKind.Trim());
            Add(insertHeader, "@no", draft.DocNo.Trim());
            Add(insertHeader, "@date", docDateText);
            Add(insertHeader, "@party", (object?)draft.PartyId ?? DBNull.Value);
            Add(insertHeader, "@partyName", (object?)draft.PartyName ?? DBNull.Value);
            Add(insertHeader, "@warehouse", (object?)draft.WarehouseId ?? DBNull.Value);
            Add(insertHeader, "@currency", string.IsNullOrWhiteSpace(draft.CurrencyCode) ? "SAR" : draft.CurrencyCode.Trim());
            // Same Step-7 rule as the sales side: the stored rate is the
            // DOCUMENT rate as given; 0 means "no document rate" and the
            // posting path resolves the effective rate through FxRates.
            Add(insertHeader, "@rate", draft.ExchangeRate < 0m ? 0m : draft.ExchangeRate);
            Add(insertHeader, "@subtotal", decimal.Round(subtotal, 2));
            Add(insertHeader, "@discount", 0m);
            Add(insertHeader, "@tax", decimal.Round(taxAmount, 2));
            Add(insertHeader, "@total", decimal.Round(grandTotal, 2));
            Add(insertHeader, "@status", CommercialDocumentStatus.Draft);
            Add(insertHeader, "@notes", (object?)draft.Notes ?? DBNull.Value);
            Add(insertHeader, "@actor", userId.Trim());
            Add(insertHeader, "@partyCode", (object?)draft.PartyCode ?? DBNull.Value);
            Add(insertHeader, "@deliveryDate", (object?)draft.DeliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
            Add(insertHeader, "@validTill", (object?)draft.ValidTill?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
            Add(insertHeader, "@costCenter", (object?)draft.CostCenter ?? DBNull.Value);
            Add(insertHeader, "@branchName", (object?)draft.BranchName ?? DBNull.Value);
            Add(insertHeader, "@salesRepName", (object?)draft.SalesRepName ?? DBNull.Value);
            Add(insertHeader, "@warehouseName", (object?)draft.WarehouseName ?? DBNull.Value);
            docId = Convert.ToInt64(await insertHeader.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

            var lineNumber = 0;
            for (var index = 0; index < draft.Lines.Count; index++)
            {
                var line = draft.Lines[index];
                var totals = computed[index];
                await using var insertLine = CreateCommand(connection, transaction,
                    "INSERT INTO purchase_document_lines (doc_id, line_no, material_id, material_code, description, unit, qty, unit_price, discount_pct, tax_rate, line_subtotal, line_tax, line_total, warehouse_id) " +
                    "VALUES (@doc, @line, @material, @materialCode, @description, @unit, @qty, @price, @discount, @taxRate, @subtotal, @tax, @total, @warehouse)");
                Add(insertLine, "@doc", docId);
                Add(insertLine, "@line", ++lineNumber);
                Add(insertLine, "@material", (object?)line.MaterialId ?? DBNull.Value);
                Add(insertLine, "@materialCode", (object?)line.MaterialCode ?? DBNull.Value);
                Add(insertLine, "@description", string.IsNullOrWhiteSpace(line.Description) ? line.MaterialCode ?? "Item" : line.Description.Trim());
                Add(insertLine, "@unit", (object?)line.Unit ?? DBNull.Value);
                Add(insertLine, "@qty", line.Qty);
                Add(insertLine, "@price", line.UnitPrice);
                Add(insertLine, "@discount", line.DiscountPct);
                Add(insertLine, "@taxRate", line.TaxRate);
                Add(insertLine, "@subtotal", totals.Subtotal);
                Add(insertLine, "@tax", totals.Tax);
                Add(insertLine, "@total", totals.Total);
                Add(insertLine, "@warehouse", (object?)line.WarehouseId ?? DBNull.Value);
                await insertLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return docId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Posts a purchase document and writes its side effects atomically:
    /// - RCV (purchase receipt): one stock movement (QtyIn, receipt cost =
    ///   line unit price) per stockable line, in base-unit quantities, plus
    ///   the balance update — through the ONE StockValuationEngine, so
    ///   valuation methods, batch/serial tracking and the balance ledger
    ///   behave exactly as the sales side does.
    /// - PUR (purchase invoice): a party_ledger credit for the supplier
    ///   and a balanced journal entry (Dr Purchases [+ input VAT when a
    ///   postable input-VAT account exists] / Cr Payable) in the same
    ///   transaction.
    /// - RFQ / PO: no side effects.
    /// </summary>
    public async Task PostPurchaseAsync(long docId, string userId, CancellationToken cancellationToken = default)
    {
        if (docId <= 0) throw new ArgumentException("Document is required.", nameof(docId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string companyId, docKind, docNo, docDate, currencyCode, status;
            string? partyCode;
            long? partyId;
            long? warehouseId;
            decimal grandTotal;
            decimal subtotal = 0m, taxAmount = 0m, exchangeRate = 1m;
            string? costCenter = null;

            await using (var header = CreateCommand(connection, transaction, """
                SELECT company_id, doc_kind, doc_no, doc_date, party_id, warehouse_id, currency_code, grand_total, status, party_code, subtotal, tax_amount, cost_center, exchange_rate
                FROM purchase_documents
                WHERE doc_id = @doc AND is_deleted = 0
                """))
            {
                Add(header, "@doc", docId);
                await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Purchase document was not found.");
                companyId = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
                docKind = reader.GetString(1);
                docNo = reader.GetString(2);
                docDate = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture)!;
                partyId = reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture);
                warehouseId = reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
                currencyCode = reader.GetString(6);
                grandTotal = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture);
                status = reader.GetString(8);
                partyCode = reader.IsDBNull(9) ? null : reader.GetString(9);
                subtotal = reader.IsDBNull(10) ? 0m : Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture);
                taxAmount = reader.IsDBNull(11) ? 0m : Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture);
                costCenter = reader.IsDBNull(12) ? null : reader.GetString(12);
                exchangeRate = reader.IsDBNull(13) ? 0m : Convert.ToDecimal(reader.GetValue(13), CultureInfo.InvariantCulture);
            }

            if (status != CommercialDocumentStatus.Draft)
                throw new InvalidOperationException("Purchase document was not found or is not in draft status.");

            if (docKind == CommercialDocKinds.PurchaseReceipt)
            {
                // The quality gate keys the company by CODE (the stock
                // world), resolved once per posting from this
                // repository's Guid key — same as the sales side.
                var qcCompany = PartyLedger.ResolveCompanyCode(connection, transaction, _provider, companyId);
                var pending = new List<(long MaterialId, decimal Qty, long WarehouseId, string? Unit, decimal UnitPrice)>();
                await using (var lines = CreateCommand(connection, transaction,
                    "SELECT material_id, qty, warehouse_id, unit, unit_price FROM purchase_document_lines WHERE doc_id = @doc ORDER BY line_no"))
                {
                    Add(lines, "@doc", docId);
                    await using var reader = await lines.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (reader.IsDBNull(0))
                            continue;
                        var lineWarehouse = reader.IsDBNull(2)
                            ? warehouseId ?? 0L
                            : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                        pending.Add((Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                                     Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
                                     lineWarehouse,
                                     reader.IsDBNull(3) ? null : reader.GetString(3),
                                     Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture)));
                    }
                }

                foreach (var (materialId, qty, lineWarehouse, lineUnit, unitPrice) in pending)
                {
                    // Service-material rule (mirrors the sales side): a
                    // service material is non-stock and creates no movement.
                    if (!await IsStockableAsync(connection, transaction, materialId, cancellationToken).ConfigureAwait(false))
                        continue;

                    // Unit equivalence: stock is kept in the material's
                    // first (base) unit; a receipt in another unit adds
                    // qty x equivalent(chosen unit) — the single rule in
                    // UnitEquivalents.
                    var baseQty = qty * await ResolveUnitEquivalentAsync(
                        connection, transaction, materialId, lineUnit, cancellationToken).ConfigureAwait(false);

                    // Incoming quality gate (Phase B 056, decision Q2,
                    // mirror of StkPost): a receipt of a material with
                    // quality_check_required lands in the QUARANTINE
                    // warehouse — not in sellable stock — and a Draft
                    // incoming inspection is created against the receipt
                    // below, in this same transaction. Completion releases
                    // it (QualityGate); a pre-056 database diverts nothing.
                    var quarantineWarehouseId = QualityGate.DivertReceipt(
                        connection, transaction, _provider, qcCompany, materialId);
                    var diverted = quarantineWarehouseId > 0;
                    var effectiveWarehouse = diverted ? quarantineWarehouseId : lineWarehouse;

                    // Valuation + batch/serial: mastered by the ONE
                    // StockValuationEngine at the material's cost method,
                    // exactly like the delivery-note receipt side.
                    var movement = StockValuationEngine.Apply(connection, transaction, _provider,
                        new StockMovementRequest(companyId, effectiveWarehouse, materialId,
                            docDate, "RCV", docNo, QtyIn: baseQty, QtyOut: 0m,
                            Note: $"Purchase receipt {docNo} posting",
                            ReceiptUnitCost: unitPrice));

                    if (diverted)
                    {
                        var batchId = movement.AllocationRecords
                            .FirstOrDefault(a => a.BatchId is not null)?.BatchId;
                        QualityGate.EnsureInspection(connection, transaction, _provider, qcCompany,
                            QualityInspectionTypes.Incoming, CommercialDocKinds.PurchaseReceipt,
                            docId.ToString(CultureInfo.InvariantCulture), docNo,
                            materialId, batchId, lineWarehouse, effectiveWarehouse,
                            partyId, baseQty, docDate, userId.Trim());
                    }
                }
            }
            else if (docKind == CommercialDocKinds.PurchaseReturn)
            {
                // Stock-out legs (the mirror of a receipt): each stockable
                // line issues the returned base-unit quantity out of the
                // line's warehouse. Receipt cost carries the return -- the
                // issue does not -- so no receipt pricing is passed; the
                // engine values the issue from the balance/layer queue, and
                // serial-tracked lines auto-pick like a chain delivery.
                var pendingLines = new List<(long MaterialId, decimal Qty, long WarehouseId, string? Unit)>();
                await using (var lines = CreateCommand(connection, transaction,
                    "SELECT material_id, qty, warehouse_id, unit FROM purchase_document_lines WHERE doc_id = @doc ORDER BY line_no"))
                {
                    Add(lines, "@doc", docId);
                    await using var reader = await lines.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (reader.IsDBNull(0))
                            continue;
                        var lineWarehouse = reader.IsDBNull(2)
                            ? warehouseId ?? 0L
                            : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                        pendingLines.Add((Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                                          Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
                                          lineWarehouse,
                                          reader.IsDBNull(3) ? null : reader.GetString(3)));
                    }
                }

                foreach (var (materialId, qty, lineWarehouse, lineUnit) in pendingLines)
                {
                    // Service-material rule (mirrors the sales side): a
                    // service material is non-stock and creates no movement.
                    if (!await IsStockableAsync(connection, transaction, materialId, cancellationToken).ConfigureAwait(false))
                        continue;

                    var baseQty = qty * await ResolveUnitEquivalentAsync(
                        connection, transaction, materialId, lineUnit, cancellationToken).ConfigureAwait(false);

                    StockValuationEngine.Apply(connection, transaction, _provider,
                        new StockMovementRequest(companyId, lineWarehouse, materialId,
                            docDate, CommercialDocKinds.PurchaseReturn, docNo,
                            QtyIn: 0m, QtyOut: baseQty,
                            Note: $"Purchase return {docNo} posting",
                            AutoAllocateSerials: true));
                }

                if (partyId is not null)
                {
                    // Effective rate resolved ONCE through FxRates — same
                    // as the purchase invoice posting path.
                    var fx = FxRates.Resolve(connection, transaction, _provider,
                        Guid.Parse(companyId), currencyCode,
                        DateOnly.Parse(docDate, CultureInfo.InvariantCulture), exchangeRate);

                    var ledgerCompany = await PartyLedger.ResolveCompanyCodeAsync(
                        connection, transaction, _provider, companyId, cancellationToken).ConfigureAwait(false);

                    // Supplier liability reversal: DEBIT the party ledger
                    // at the grand total — the exact mirror of the invoice
                    // credit — so the supplier balance shrinks.
                    PartyLedger.WriteEntry(connection, transaction, _provider,
                        ledgerCompany, "S", partyId.Value,
                        CommercialDocKinds.PurchaseReturn, docId, docNo, docDate,
                        grandTotal, 0m, currencyCode,
                        $"Purchase return {docNo} posting",
                        exchangeRate: fx.Rate);
                }
            }

            if (docKind == CommercialDocKinds.PurchaseInvoice && partyId is not null)
            {
                // Effective rate resolved ONCE through FxRates — same as
                // the sales invoice posting path.
                var fx = FxRates.Resolve(connection, transaction, _provider,
                    Guid.Parse(companyId), currencyCode,
                    DateOnly.Parse(docDate, CultureInfo.InvariantCulture), exchangeRate);

                var ledgerCompany = await PartyLedger.ResolveCompanyCodeAsync(
                    connection, transaction, _provider, companyId, cancellationToken).ConfigureAwait(false);

                // Supplier liability: credit the party ledger at the grand
                // total, written through PartyLedger.WriteEntry so base
                // amounts and the effective rate travel with the row.
                PartyLedger.WriteEntry(connection, transaction, _provider,
                    ledgerCompany, "S", partyId.Value,
                    CommercialDocKinds.PurchaseInvoice, docId, docNo, docDate,
                    0m, grandTotal, currencyCode,
                    $"Purchase invoice {docNo} posting",
                    exchangeRate: fx.Rate);
            }

            // Real GL posting: a posted chain PURCHASE INVOICE or PURCHASE
            // RETURN creates its balanced journal entry in this same
            // transaction. PUR: Dr Purchases [+ Dr Input VAT when postable]
            // / Cr Payable. PRT is the exact mirror (Dr Payable / Cr
            // Purchase Returns [+ Cr Input VAT]). RCV/RFQ/PO keep no GL
            // entry.
            if ((docKind == CommercialDocKinds.PurchaseInvoice || docKind == CommercialDocKinds.PurchaseReturn)
                && grandTotal > 0)
            {
                var companyGuid = Guid.Parse(companyId);
                var splitTax = taxAmount > 0
                    && GlPosting.TryResolveAccount(connection, transaction, _provider, companyGuid, GlAccounts.InputVat) is not null;
                var isReturn = docKind == CommercialDocKinds.PurchaseReturn;
                var glLines = new List<GlLineRequest>();
                if (isReturn)
                {
                    glLines.Add(new GlLineRequest(GlAccounts.Payable, grandTotal, 0m));
                    glLines.Add(new GlLineRequest(GlAccounts.PurchaseReturns, 0m, splitTax ? subtotal : grandTotal, costCenter));
                }
                else
                {
                    glLines.Add(new GlLineRequest(GlAccounts.Payable, 0m, grandTotal));
                    glLines.Add(new GlLineRequest(GlAccounts.Purchases, splitTax ? subtotal : grandTotal, 0m, costCenter));
                }
                if (splitTax)
                    glLines.Add(isReturn
                        ? new GlLineRequest(GlAccounts.InputVat, 0m, taxAmount)
                        : new GlLineRequest(GlAccounts.InputVat, taxAmount, 0m));
                GlPosting.PostInTransaction(connection, transaction, _provider,
                    new GlDocumentRequest(companyGuid,
                        DateOnly.Parse(docDate, CultureInfo.InvariantCulture),
                        isReturn ? $"PRT {docNo}" : $"PUR {docNo}", docKind,
                        docId.ToString(CultureInfo.InvariantCulture), docNo,
                        currencyCode, exchangeRate, glLines),
                    userId);
            }

            await using var update = CreateCommand(connection, transaction,
                "UPDATE purchase_documents SET status = @posted, posted_by = @actor, posted_at = CURRENT_TIMESTAMP WHERE doc_id = @doc AND status = @draft AND is_deleted = 0");
            Add(update, "@posted", CommercialDocumentStatus.Posted);
            Add(update, "@actor", userId.Trim());
            Add(update, "@doc", docId);
            Add(update, "@draft", CommercialDocumentStatus.Draft);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Purchase document was not found or is not in draft status.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task AddLinkAsync(Guid companyId, string sourceDocKind, long sourceDocId, string targetDocKind, long targetDocId, string userId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(sourceDocKind) || string.IsNullOrWhiteSpace(targetDocKind))
            throw new ArgumentException("Source and target document kinds are required.");
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO document_links (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by)
            VALUES (@company, @sourceKind, @sourceId, @targetKind, @targetId, @actor)
            """;
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@sourceKind", sourceDocKind.Trim());
        Add(command, "@sourceId", sourceDocId);
        Add(command, "@targetKind", targetDocKind.Trim());
        Add(command, "@targetId", targetDocId);
        Add(command, "@actor", userId.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CommercialDocumentLink>> GetPurchaseLinksFromAsync(Guid companyId, long sourceDocId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT link_id, company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id
            FROM document_links
            WHERE company_id = @company AND source_doc_id = @sourceId
            ORDER BY link_id
            """;
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@sourceId", sourceDocId);

        var links = new List<CommercialDocumentLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            links.Add(new CommercialDocumentLink(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Guid.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!),
                reader.GetString(2),
                Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.GetString(4),
                Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture)));
        }
        return links.AsReadOnly();
    }

    public async Task<IReadOnlyDictionary<long, decimal>> GetPurchaseFulfilledQuantitiesAsync(Guid companyId, long sourceDocId, string targetDocKind, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(targetDocKind)) throw new ArgumentException("Target document kind is required.", nameof(targetDocKind));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.material_id, SUM(l.qty)
            FROM document_links dl
            JOIN purchase_documents d
              ON d.company_id = dl.company_id
             AND d.doc_id = dl.target_doc_id
             AND d.doc_kind = dl.target_doc_kind
            JOIN purchase_document_lines l
              ON l.doc_id = d.doc_id
            WHERE dl.company_id = @company
              AND dl.source_doc_id = @sourceId
              AND dl.target_doc_kind = @targetKind
              AND d.status = @posted
              AND d.is_deleted = 0
            GROUP BY l.material_id
            """;
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@sourceId", sourceDocId);
        Add(command, "@targetKind", targetDocKind.Trim());
        Add(command, "@posted", CommercialDocumentStatus.Posted);

        var quantities = new Dictionary<long, decimal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
                continue;
            var materialId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            quantities[materialId] = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }
        return quantities;
    }

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    /// <summary>Unit equivalence, resolved by the ONE UnitEquivalents rule (sales side shares it).</summary>
    private static Task<decimal> ResolveUnitEquivalentAsync(
        DbConnection connection, DbTransaction transaction, long materialId, string? unitName,
        CancellationToken cancellationToken)
        => UnitEquivalents.ResolveAsync(connection, transaction, null, materialId, unitName, cancellationToken);

    /// <summary>Whether the material is stock-tracked (same guard as the sales side).</summary>
    private static async Task<bool> IsStockableAsync(
        DbConnection connection, DbTransaction transaction, long materialId,
        CancellationToken cancellationToken)
    {
        await using var cmd = CreateCommand(connection, transaction, """
            SELECT is_stocked, is_service, item_type
            FROM materials
            WHERE material_id = @material
            """);
        Add(cmd, "@material", materialId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return true;

        var isStocked = !reader.IsDBNull(0) && Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture);
        var isService = !reader.IsDBNull(1) && Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
        var itemType = reader.IsDBNull(2) ? null : reader.GetValue(2)?.ToString();
        return isStocked
               && !isService
               && !string.Equals(itemType, "Service", StringComparison.OrdinalIgnoreCase);
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
