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

/// <summary>Provider-neutral persistence for sales documents and document chain links (SQLite and PostgreSQL).</summary>
public sealed class CommercialDocumentRepository : ICommercialDocumentRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public CommercialDocumentRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    public async Task<CommercialDocumentSnapshot?> FindSalesByIdAsync(long docId, CancellationToken cancellationToken = default)
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
                FROM sales_documents
                WHERE doc_id = @docId AND is_deleted = 0
                """;
            Add(header, "@docId", docId);
            await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            foundId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (!Guid.TryParse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture), out companyId) || companyId == Guid.Empty)
                throw new InvalidOperationException("The saved sales document has an invalid company identifier.");
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
                FROM sales_document_lines
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

    public async Task<long> CreateSalesDraftAsync(CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default)
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
            var docId = await CreateSalesDraftInTransactionAsync(connection, transaction, draft, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return docId;
        }
        catch
        {
            // Preserve the original database/business exception. A provider may
            // already have completed or aborted the transaction; rollback is
            // best-effort and disposal remains the final cleanup guarantee.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Transaction is already completed; do not mask the root error.
            }
            throw;
        }
    }

    public async Task<long> CreateSalesDraftWithLinkAsync(
        CommercialDocumentDraft draft,
        string sourceDocKind,
        long sourceDocId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.CompanyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.DocKind)) throw new ArgumentException("Document kind is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.DocNo)) throw new ArgumentException("Document number is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        if (draft.Lines.Count == 0) throw new InvalidOperationException("A commercial document requires at least one line.");
        if (sourceDocId <= 0) throw new ArgumentException("Source document is required.", nameof(sourceDocId));
        if (string.IsNullOrWhiteSpace(sourceDocKind)) throw new ArgumentException("Source document kind is required.", nameof(sourceDocKind));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var source = CreateCommand(connection, transaction, """
                SELECT status, doc_kind, company_id
                FROM sales_documents
                WHERE doc_id = @sourceId AND is_deleted = 0
                """))
            {
                Add(source, "@sourceId", sourceDocId);
                await using var reader = await source.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Source sales document was not found.");
                var sourceStatus = reader.GetString(0);
                var persistedSourceKind = reader.GetString(1);
                var persistedCompanyId = reader.GetString(2);
                if (!string.Equals(sourceStatus, CommercialDocumentStatus.Posted, StringComparison.Ordinal))
                    throw new InvalidOperationException("Only posted source documents can be linked to a new sales document.");
                if (!string.Equals(persistedSourceKind, sourceDocKind.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException("The source document kind changed before the linked document was saved.");
                if (!string.Equals(persistedCompanyId, draft.CompanyId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Source and target documents must belong to the same company.");
            }

            var docId = await CreateSalesDraftInTransactionAsync(connection, transaction, draft, userId, cancellationToken).ConfigureAwait(false);
            await using var link = CreateCommand(connection, transaction, """
                INSERT INTO document_links
                    (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id, created_by)
                VALUES (@company, @sourceKind, @sourceId, @targetKind, @targetId, @actor)
                """);
            Add(link, "@company", draft.CompanyId.ToString("D"));
            Add(link, "@sourceKind", sourceDocKind.Trim());
            Add(link, "@sourceId", sourceDocId);
            Add(link, "@targetKind", draft.DocKind.Trim());
            Add(link, "@targetId", docId);
            Add(link, "@actor", userId.Trim());
            await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return docId;
        }
        catch
        {
            // Preserve the original database/business exception. A provider may
            // already have completed or aborted the transaction; rollback is
            // best-effort and disposal remains the final cleanup guarantee.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Transaction is already completed; do not mask the root error.
            }
            throw;
        }
    }

    private async Task<long> CreateSalesDraftInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        CommercialDocumentDraft draft,
        string userId,
        CancellationToken cancellationToken)
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
            "INSERT INTO sales_documents (company_id, doc_kind, doc_no, doc_date, party_id, party_name, warehouse_id, currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total, status, notes, created_by, party_code, delivery_date, valid_till, cost_center, branch_name, sales_rep_name, warehouse_name) " +
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
        // Step 7: the stored rate is the DOCUMENT rate as given —
        // 0 stays 0 ("no document rate"), so posting can fall back
        // to the currency_rates master or fail with ACC-5015. The
        // pre-Step-7 coercion to 1 fabricated a rate that was never
        // entered and made the master table unreachable.
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
                "INSERT INTO sales_document_lines (doc_id, line_no, material_id, material_code, description, unit, qty, unit_price, discount_pct, tax_rate, line_subtotal, line_tax, line_total, warehouse_id) " +
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

        // The caller owns the transaction boundary. Keeping this method
        // transaction-neutral ensures the header, lines and optional source
        // link commit or roll back as one atomic unit.
        return docId;
    }

    /// <summary>
    /// Posts a sales document and writes its accounting side effects atomically:
    /// - DLV (delivery note): one material_stock_movements row per stock line and a
    ///   matching decrement (or initialization) of material_stock_balances.
    /// - SAL (sales invoice): a party_ledger debit for the customer grand total.
    /// - QUO / SO: no side effects.
    /// </summary>
    public async Task PostSalesAsync(long docId, string userId, CancellationToken cancellationToken = default)
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
                FROM sales_documents
                WHERE doc_id = @doc AND is_deleted = 0
                """))
            {
                Add(header, "@doc", docId);
                await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Sales document was not found.");
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
                // Step 7: a stored rate of 0 means "no document rate" —
                // it is NOT coerced to 1 here. Posting resolves the
                // effective rate through FxRates (document rate, else
                // currency_rates master, else ACC-5015).
                exchangeRate = reader.IsDBNull(13) ? 0m : Convert.ToDecimal(reader.GetValue(13), CultureInfo.InvariantCulture);
            }

            if (status != CommercialDocumentStatus.Draft)
                throw new InvalidOperationException("Sales document was not found or is not in draft status.");

            if (docKind == CommercialDocKinds.DeliveryNote)
            {
                await EnsureDeliveryWithinSalesOrderAsync(
                    connection, transaction, docId, companyId, cancellationToken).ConfigureAwait(false);

                var pending = new List<(long MaterialId, decimal Qty, long WarehouseId, string? Unit)>();
                await using (var lines = CreateCommand(connection, transaction,
                    "SELECT material_id, qty, warehouse_id, unit FROM sales_document_lines WHERE doc_id = @doc ORDER BY line_no"))
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
                                     reader.IsDBNull(3) ? null : reader.GetString(3)));
                    }
                }

                foreach (var (materialId, qty, lineWarehouse, lineUnit) in pending)
                {
                    // Service-material rule (owner, 2026-11-17): a service
                    // material is non-stock — a delivery note for it creates
                    // no movement and no balance change. (A material with no
                    // card row keeps the historical behavior and posts.)
                    if (!await IsStockableAsync(connection, transaction, materialId, cancellationToken).ConfigureAwait(false))
                        continue;

                    // Unit equivalence (owner definition, 2026-11-17): stock is
                    // kept in the material's first (base) unit, so a delivery in
                    // another unit deducts qty x equivalent(chosen unit) — the
                    // same rule StkPost applies to invoice documents.
                    var baseQty = qty * await ResolveUnitEquivalentAsync(
                        connection, transaction, materialId, lineUnit, cancellationToken).ConfigureAwait(false);
                    // Outgoing quality gate (Phase B 056, decision Q4):
                    // a delivery of an outgoing-required material needs
                    // an accepted outgoing inspection — Stop refuses
                    // (INV-6013), Warn posts and raises the pending
                    // inspection; a Rejected inspection blocks in both
                    // modes (INV-6011). The gate keys the company by
                    // CODE (the stock world), resolved from this
                    // repository's Guid key.
                    var qcCompany = PartyLedger.ResolveCompanyCode(connection, transaction, _provider, companyId);
                    var qcProfile = QualityGate.LoadProfile(connection, transaction, _provider, qcCompany, materialId);
                    if (qcProfile.OutgoingRequired)
                    {
                        QualityGate.CheckOutgoing(connection, transaction, _provider, qcCompany,
                            "DLV", docId.ToString(CultureInfo.InvariantCulture), docNo,
                            materialId, lineWarehouse, partyId, docDate, userId);
                    }
                    // Valuation (Step 6, Phase B 053): the delivery note
                    // is valued by the ONE StockValuationEngine at the
                    // material's cost method — the pre-engine code wrote
                    // unit_cost = 0 and never touched average_cost, so
                    // chain deliveries carried no cost at all (RC-1).
                    // Batch/serial (Phase B 054): batch issues
                    // auto-allocate FEFO/FIFO inside the engine; the
                    // chain has no serial capture UI, so serial-tracked
                    // lines auto-pick the oldest in-stock serials
                    // (AutoAllocateSerials) — recorded either way in
                    // stock_movement_allocations.
                    StockValuationEngine.Apply(connection, transaction, _provider,
                        new StockMovementRequest(companyId, lineWarehouse, materialId,
                            docDate, "DLV", docNo, QtyIn: 0m, QtyOut: baseQty,
                            Note: $"Delivery note {docNo} posting",
                            AutoAllocateSerials: true));
                }
            }
            else if (docKind == CommercialDocKinds.SalesInvoice && partyId is not null)
            {
                // Step 7 (J-5): resolve the effective rate ONCE through
                // FxRates (document rate, else master, else ACC-5015);
                // the guard and the ledger both consume BASE amounts at
                // this rate, and GlPosting below resolves identically
                // from the same stored inputs.
                var fx = FxRates.Resolve(connection, transaction, _provider,
                    Guid.Parse(companyId), currencyCode,
                    DateOnly.Parse(docDate, CultureInfo.InvariantCulture), exchangeRate);

                // Credit-limit gate (ACC-5006): the ledger debit below must
                // not push the customer over the limit on their party card.
                // Step 7: the guard sums base amounts, so it is fed the
                // document's BASE total, not the raw foreign total.
                await PartyCreditGuard.EnsureWithinLimitAsync(
                    connection, transaction, _provider, companyId, "C", partyId.Value, partyCode,
                    FxRates.ToBase(grandTotal, fx.Rate), "@", cancellationToken).ConfigureAwait(false);

                // Step 4 (B-06): party_ledger.company_id is the company
                // CODE; this repository's documents key the company by
                // Guid, so resolve before writing (the guard above accepts
                // both of the company's own keys).
                var ledgerCompany = await PartyLedger.ResolveCompanyCodeAsync(
                    connection, transaction, _provider, companyId, cancellationToken).ConfigureAwait(false);

                // Step 7: written through PartyLedger.WriteEntry so the
                // row carries base_debit/base_credit + the effective
                // rate alongside the transaction amounts.
                PartyLedger.WriteEntry(connection, transaction, _provider,
                    ledgerCompany, "C", partyId.Value,
                    CommercialDocKinds.SalesInvoice, docId, docNo, docDate,
                    grandTotal, 0m, currencyCode,
                    $"Sales invoice {docNo} posting",
                    exchangeRate: fx.Rate);
            }

            // Real GL posting (Step 3, J-2): a posted chain SALES INVOICE
            // creates its balanced journal entry in this same transaction
            // (Dr Receivable / Cr Revenue, tax split only when a postable
            // VAT account is configured). DLV/QUO/SO keep no GL entry —
            // the delivery note moves stock without amounts, and the
            // invoice is the chain's accounting document.
            if (docKind == CommercialDocKinds.SalesInvoice && grandTotal > 0)
            {
                var companyGuid = Guid.Parse(companyId);
                var splitTax = taxAmount > 0
                    && GlPosting.TryResolveAccount(connection, transaction, _provider, companyGuid, GlAccounts.OutputVat) is not null;
                var glLines = new List<GlLineRequest>
                {
                    new(GlAccounts.Receivable, grandTotal, 0m),
                    new(GlAccounts.SalesRevenue, 0m, splitTax ? subtotal : grandTotal, costCenter)
                };
                if (splitTax)
                    glLines.Add(new GlLineRequest(GlAccounts.OutputVat, 0m, taxAmount));
                GlPosting.PostInTransaction(connection, transaction, _provider,
                    new GlDocumentRequest(companyGuid,
                        DateOnly.Parse(docDate, CultureInfo.InvariantCulture),
                        $"SAL {docNo}", CommercialDocKinds.SalesInvoice,
                        docId.ToString(CultureInfo.InvariantCulture), docNo,
                        currencyCode, exchangeRate, glLines),
                    userId);
            }

            await using var update = CreateCommand(connection, transaction,
                "UPDATE sales_documents SET status = @posted, posted_by = @actor, posted_at = CURRENT_TIMESTAMP WHERE doc_id = @doc AND status = @draft AND is_deleted = 0");
            Add(update, "@posted", CommercialDocumentStatus.Posted);
            Add(update, "@actor", userId.Trim());
            Add(update, "@doc", docId);
            Add(update, "@draft", CommercialDocumentStatus.Draft);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Sales document was not found or is not in draft status.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original database/business exception. A provider may
            // already have completed or aborted the transaction; rollback is
            // best-effort and disposal remains the final cleanup guarantee.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Transaction is already completed; do not mask the root error.
            }
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

    public async Task<IReadOnlyList<CommercialDocumentLink>> GetSalesLinksFromAsync(Guid companyId, long sourceDocId, CancellationToken cancellationToken = default)
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

    public async Task<IReadOnlyDictionary<long, decimal>> GetFulfilledQuantitiesAsync(Guid companyId, long sourceDocId, string targetDocKind, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(targetDocKind)) throw new ArgumentException("Target document kind is required.", nameof(targetDocKind));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.material_id, SUM(l.qty)
            FROM document_links dl
            JOIN sales_documents d
              ON d.company_id = dl.company_id
             AND d.doc_id = dl.target_doc_id
             AND d.doc_kind = dl.target_doc_kind
            JOIN sales_document_lines l
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

    /// <summary>
    /// Resolves the equivalence of a line's chosen unit relative to the
    /// material's first (base) unit, from the unit rows saved on the material
    /// card (units_json carries the complete list; the unit1..unit5 column
    /// pairs are the fallback for legacy rows). Mirrors the rule StkPost
    /// applies to invoice documents: no unit chosen, the base unit itself, an
    /// unknown or free-typed unit, or a stored equivalent of 0 or less all
    /// resolve to 1. The connection is already scoped to one company's
    /// database, so the lookup keys on material_id alone.
    /// </summary>
    private static Task<decimal> ResolveUnitEquivalentAsync(
        DbConnection connection, DbTransaction transaction, long materialId, string? unitName,
        CancellationToken cancellationToken)
        // Step 11: the rule lives ONCE in UnitEquivalents (shared with
        // MfgOrderRepository and OrderDocumentRepository); this wrapper
        // keeps the chain repository's material_id-only scoping.
        => UnitEquivalents.ResolveAsync(connection, transaction, null, materialId, unitName, cancellationToken);

    /// <summary>
    /// Whether the material is stock-tracked, mirroring the guard StkPost
    /// applies to invoice documents: not stocked, flagged as a service, or
    /// typed as a Service (any one of the three views the material card
    /// keeps in agreement) means non-stock. A material with no card row
    /// keeps the historical chain behavior and is treated as stockable.
    /// </summary>
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

    /// <summary>
    /// Enforces the source sales-order quantity ceiling at delivery posting time.
    /// Draft delivery notes do not consume order quantity; the guard runs inside
    /// the same transaction as stock posting so a second concurrent dispatch
    /// cannot post against quantities already delivered by the first.
    /// </summary>
    private async Task EnsureDeliveryWithinSalesOrderAsync(
        DbConnection connection,
        DbTransaction transaction,
        long deliveryDocId,
        string companyId,
        CancellationToken cancellationToken)
    {
        long? sourceOrderId = null;
        await using (var sourceLink = CreateCommand(connection, transaction, """
            SELECT source_doc_id
            FROM document_links
            WHERE company_id = @company
              AND target_doc_kind = @deliveryKind
              AND target_doc_id = @deliveryId
              AND source_doc_kind = @orderKind
            """))
        {
            Add(sourceLink, "@company", companyId);
            Add(sourceLink, "@deliveryKind", CommercialDocKinds.DeliveryNote);
            Add(sourceLink, "@deliveryId", deliveryDocId);
            Add(sourceLink, "@orderKind", CommercialDocKinds.SalesOrder);
            var value = await sourceLink.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
                sourceOrderId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        // A manually entered, unlinked delivery note follows the legacy path.
        // Linked deliveries must always respect their originating posted order.
        if (sourceOrderId is null)
            return;

        await using (var orderLock = CreateCommand(connection, transaction,
            _provider == "PostgreSQL"
                ? """
                  SELECT doc_id
                  FROM sales_documents
                  WHERE doc_id = @orderId AND company_id = @company
                    AND doc_kind = @orderKind AND status = @posted
                    AND CAST(is_deleted AS TEXT) IN ('0', 'false')
                  FOR UPDATE
                  """
                : """
                  SELECT doc_id
                  FROM sales_documents
                  WHERE doc_id = @orderId AND company_id = @company
                    AND doc_kind = @orderKind AND status = @posted
                    AND CAST(is_deleted AS TEXT) IN ('0', 'false')
                  """))
        {
            Add(orderLock, "@orderId", sourceOrderId.Value);
            Add(orderLock, "@company", companyId);
            Add(orderLock, "@orderKind", CommercialDocKinds.SalesOrder);
            Add(orderLock, "@posted", CommercialDocumentStatus.Posted);
            var locked = await orderLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (locked is null or DBNull)
                throw new InvalidOperationException("The source sales order is missing, cancelled, or not posted.");
        }

        async Task<Dictionary<(long MaterialId, string Unit), decimal>> ReadQuantitiesAsync(
            string sql, params (string Name, object Value)[] parameters)
        {
            var result = new Dictionary<(long MaterialId, string Unit), decimal>();
            await using var command = CreateCommand(connection, transaction, sql);
            foreach (var (name, value) in parameters)
                Add(command, name, value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var materialId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                var unit = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
                result[(materialId, unit)] = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
            }
            return result;
        }

        const string groupByMaterialAndUnit = """
            SELECT COALESCE(material_id, -1), COALESCE(unit, ''), SUM(qty)
            FROM sales_document_lines
            WHERE doc_id = @docId
            GROUP BY material_id, unit
            """;

        var ordered = await ReadQuantitiesAsync(groupByMaterialAndUnit, ("@docId", sourceOrderId.Value)).ConfigureAwait(false);
        var current = await ReadQuantitiesAsync(groupByMaterialAndUnit, ("@docId", deliveryDocId)).ConfigureAwait(false);
        var previouslyDelivered = await ReadQuantitiesAsync("""
            SELECT COALESCE(line.material_id, -1), COALESCE(line.unit, ''), SUM(line.qty)
            FROM document_links link
            INNER JOIN sales_documents delivery
                ON delivery.company_id = link.company_id
               AND delivery.doc_id = link.target_doc_id
               AND delivery.doc_kind = link.target_doc_kind
            INNER JOIN sales_document_lines line ON line.doc_id = delivery.doc_id
            WHERE link.company_id = @company
              AND link.source_doc_kind = @orderKind
              AND link.source_doc_id = @orderId
              AND link.target_doc_kind = @deliveryKind
              AND delivery.status = @posted
              AND delivery.is_deleted = 0
              AND delivery.doc_id <> @currentId
            GROUP BY line.material_id, line.unit
            """,
            ("@company", companyId),
            ("@orderKind", CommercialDocKinds.SalesOrder),
            ("@orderId", sourceOrderId.Value),
            ("@deliveryKind", CommercialDocKinds.DeliveryNote),
            ("@posted", CommercialDocumentStatus.Posted),
            ("@currentId", deliveryDocId)).ConfigureAwait(false);

        foreach (var (key, currentQty) in current)
        {
            var orderedQty = ordered.GetValueOrDefault(key);
            var deliveredQty = previouslyDelivered.GetValueOrDefault(key);
            if (orderedQty <= 0m || deliveredQty + currentQty > orderedQty)
            {
                var materialLabel = key.MaterialId < 0
                    ? "unassigned material"
                    : key.MaterialId.ToString(CultureInfo.InvariantCulture);
                throw new InvalidOperationException(
                    $"Delivery quantity for material '{materialLabel}' and unit '{key.Unit}' exceeds the remaining quantity on sales order '{sourceOrderId}'.");
            }
        }
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
