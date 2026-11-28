using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Transactional landed-cost vouchers. Draft allocations are based on the
/// exact receipt stock movements; posting revalues the matching cost layers,
/// recognizes the consumed portion in COGS, and posts AP/GL atomically.
///
/// Completion (migration 108): charges carry a per-charge payee and credit
/// mode (Payable / AlreadyBooked); the inventory/consumed split is stored
/// per allocation at post time and replayed exactly by reversals; a
/// voucher may draw lines from several receipts with per-line exclusion.
/// </summary>
public sealed class PurchaseLandedCostRepository : IPurchaseLandedCostRepository
{
    private readonly string _provider;
    private readonly string _connectionString;
    private bool Pg => _provider == "PostgreSQL";

    public PurchaseLandedCostRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = GlPosting.NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<PurchaseLandedCostReceiptOption>> ListReceiptsAsync(
        Guid companyId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<PurchaseLandedCostReceiptOption>();
        await using var command = Cmd(connection, null, """
            SELECT doc_id, doc_no, doc_date, party_id, party_name, currency_code, exchange_rate
            FROM purchase_documents
            WHERE company_id = @company AND doc_kind = 'RCV' AND status = 'Posted' AND is_deleted = @false
            ORDER BY doc_date DESC, doc_id DESC
            LIMIT 300
            """);
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@false", Pg ? false : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PurchaseLandedCostReceiptOption(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Text(reader.GetValue(1)) ?? "",
                DateOnly.Parse(Text(reader.GetValue(2)) ?? "", CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : Text(reader.GetValue(4)),
                Text(reader.GetValue(5)) ?? "SAR",
                Dec(reader.GetValue(6))));
        }
        return rows;
    }

    public async Task<IReadOnlyList<PurchaseLandedCostReceiptLineOption>> ListReceiptLinesAsync(
        Guid companyId, long receiptDocumentId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiptDocumentId);
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var company = companyId.ToString("D");
        string receiptNo;
        using (var header = Cmd(connection, null, """
            SELECT doc_no FROM purchase_documents
            WHERE company_id = @company AND doc_id = @receipt
              AND doc_kind = 'RCV' AND status = 'Posted' AND is_deleted = @false
            """))
        {
            Add(header, "@company", company);
            Add(header, "@receipt", receiptDocumentId);
            Add(header, "@false", Pg ? false : 0);
            var value = await header.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null or DBNull)
                throw new InvalidOperationException("A posted goods receipt in the selected company is required.");
            receiptNo = Text(value) ?? string.Empty;
        }
        var stockCompanyCode = PartyLedger.ResolveCompanyCode(connection, null, _provider, company);
        var rows = new List<PurchaseLandedCostReceiptLineOption>();
        using var command = Cmd(connection, null, $"""
            SELECT l.line_id, l.line_no, l.material_id, l.material_code, l.description,
                   sm.qty_in, sm.cost_amount, COUNT(sm.stock_movement_id)
            FROM purchase_document_lines l
            JOIN material_stock_movements sm
              ON sm.company_id = @stockCompany AND sm.source_document_line_id = l.line_id
             AND sm.document_no = @receiptNo AND sm.movement_type = 'RCV'
             AND sm.qty_in > 0 AND sm.is_deleted = {(Pg ? "FALSE" : "0")}
            WHERE l.doc_id = @receipt
            GROUP BY l.line_id, l.line_no, l.material_id, l.material_code, l.description, sm.qty_in, sm.cost_amount
            ORDER BY l.line_no
            """);
        Add(command, "@stockCompany", stockCompanyCode);
        Add(command, "@receiptNo", receiptNo);
        Add(command, "@receipt", receiptDocumentId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // Only lines mapping to exactly one active stock movement are
            // eligible for landed costs (the same rule posting enforces);
            // service / non-stock lines never appear here.
            if (Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) != 1) continue;
            rows.Add(new PurchaseLandedCostReceiptLineOption(
                receiptDocumentId, receiptNo,
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : Text(reader.GetValue(3)),
                Text(reader.GetValue(4)) ?? "",
                Dec(reader.GetValue(5)), Math.Abs(Dec(reader.GetValue(6)))));
        }
        return rows;
    }

    public async Task<IReadOnlyList<PurchaseLandedCostPayeeOption>> ListPayeesAsync(
        Guid companyId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var stockCompanyCode = PartyLedger.ResolveCompanyCode(connection, null, _provider, companyId.ToString("D"));
        var rows = new List<PurchaseLandedCostPayeeOption>();
        await using var command = Cmd(connection, null, """
            SELECT ledger_id, code, COALESCE(NULLIF(name_en, ''), code), kind
            FROM parties
            WHERE company_id = @company AND is_deleted = @false AND ledger_id IS NOT NULL
            ORDER BY kind, code
            """);
        Add(command, "@company", stockCompanyCode);
        Add(command, "@false", Pg ? false : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PurchaseLandedCostPayeeOption(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : Text(reader.GetValue(1)),
                Text(reader.GetValue(2)) ?? "",
                Text(reader.GetValue(3)) ?? "S"));
        }
        return rows;
    }

    public async Task<IReadOnlyList<PurchaseLandedCostLinkableDocumentOption>> ListLinkableDocumentsAsync(
        Guid companyId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<PurchaseLandedCostLinkableDocumentOption>();
        await using var command = Cmd(connection, null, """
            SELECT d.doc_id, d.doc_no, d.doc_date, d.party_id, d.party_name, d.grand_total
            FROM purchase_documents d
            WHERE d.company_id = @company AND d.doc_kind = 'PUR' AND d.status = 'Posted'
              AND d.is_deleted = @false AND d.party_id IS NOT NULL
              AND NOT EXISTS (
                    SELECT 1 FROM purchase_landed_cost_charges c
                    JOIN purchase_landed_costs h ON h.landed_cost_id = c.landed_cost_id
                    WHERE c.linked_document_id = d.doc_id AND h.company_id = d.company_id
                      AND h.status = 'Posted'
                      AND NOT EXISTS (SELECT 1 FROM purchase_landed_costs o
                                      WHERE o.company_id = h.company_id
                                        AND o.source_reversal_id = h.landed_cost_id))
            ORDER BY d.doc_date DESC, d.doc_id DESC
            LIMIT 300
            """);
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@false", Pg ? false : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PurchaseLandedCostLinkableDocumentOption(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Text(reader.GetValue(1)) ?? "",
                DateOnly.Parse(Text(reader.GetValue(2)) ?? "", CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : Text(reader.GetValue(4)),
                Dec(reader.GetValue(5))));
        }
        return rows;
    }

    /// <summary>Returns the doc_no of the posted, non-reversal landed-cost
    /// voucher currently consuming the given supplier document, or null.
    /// Reversal vouchers never consume: reversing releases the link.</summary>
    private string? FindConsumingVoucher(DbConnection connection, DbTransaction transaction,
        string company, long linkedDocumentId, Guid? excludeVoucherId)
    {
        using var command = Cmd(connection, transaction, """
            SELECT h.doc_no FROM purchase_landed_cost_charges c
            JOIN purchase_landed_costs h ON h.landed_cost_id = c.landed_cost_id
            WHERE c.linked_document_id = @doc AND h.company_id = @company AND h.status = 'Posted'
              AND (@exclude = '' OR h.landed_cost_id <> @excludeId)
              AND NOT EXISTS (SELECT 1 FROM purchase_landed_costs o
                              WHERE o.company_id = h.company_id
                                AND o.source_reversal_id = h.landed_cost_id)
            LIMIT 1
            """);
        Add(command, "@doc", linkedDocumentId);
        Add(command, "@company", company);
        Add(command, "@exclude", excludeVoucherId is null ? "" : "x");
        Add(command, "@excludeId", excludeVoucherId is null ? (object)DBNull.Value : Id(excludeVoucherId.Value));
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Text(value);
    }

    /// <summary>
    /// Resolves a Linked charge's supplier document: it must be a posted
    /// purchase document of this company with a supplier, not already
    /// consumed by a posted voucher, in the voucher's currency, and its
    /// posted journal must debit exactly ONE cost account (clearing and
    /// control accounts excluded) — that account, read from the journal
    /// rather than guessed, becomes the reclass credit account. The
    /// charge amount may not exceed that debit.
    /// </summary>
    private (long SupplierId, string AccountCode) ResolveLinkedCharge(
        DbConnection connection, DbTransaction transaction, string company,
        PurchaseLandedCostChargeDraft charge, string currencyCode)
    {
        var docId = charge.LinkedDocumentId!.Value;
        var chargeCode = charge.ChargeCode.Trim();
        AppError Invalid() => new(ErrorCodes.LandedCostLinkedDocumentInvalid,
            $"Charge '{chargeCode}' cannot link to supplier document {docId}.", chargeCode, docId);

        string docKind;
        long supplierId;
        using (var doc = Cmd(connection, transaction, """
            SELECT doc_kind, party_id FROM purchase_documents
            WHERE company_id = @company AND doc_id = @doc AND status = 'Posted' AND is_deleted = @false
            """))
        {
            Add(doc, "@company", company);
            Add(doc, "@doc", docId);
            Add(doc, "@false", Pg ? false : 0);
            using var reader = doc.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(1)) throw Invalid();
            docKind = Text(reader.GetValue(0)) ?? "";
            supplierId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        }
        // The public picker lists posted supplier purchase invoices only.
        // Enforce the same boundary here: callers can bypass the picker and
        // submit arbitrary document IDs, so a posted receipt, return, order,
        // or other purchase document must never be accepted as the source
        // liability for a landed-cost reclassification.
        if (supplierId <= 0 || !string.Equals(docKind, "PUR", StringComparison.OrdinalIgnoreCase))
            throw Invalid();

        var consuming = FindConsumingVoucher(connection, transaction, company, docId, null);
        if (consuming is not null)
            throw new AppError(ErrorCodes.LandedCostDocumentAlreadyLinked,
                $"Supplier document {docId} is already consumed by posted landed-cost voucher '{consuming}'.",
                consuming, docId);

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            GlAccounts.Inventory, GlAccounts.Payable, GlAccounts.GoodsReceivedNotInvoiced,
            GlAccounts.InputVat, GlAccounts.Receivable, GlAccounts.PurchasePriceVariance
        };
        var candidates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        string? journalCurrency = null;
        using (var journal = Cmd(connection, transaction, """
            SELECT a.account_code, COALESCE(SUM(l.debit), 0), e.currency_code
            FROM journal_entries e
            JOIN journal_entry_lines l ON l.journal_entry_id = e.journal_entry_id
            JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = e.company_id
            WHERE e.company_id = @company AND e.source_doc_type = @kind AND e.source_doc_id = @doc
              AND e.status = 1 AND e.reversal_of_entry_id IS NULL
            GROUP BY a.account_code, e.currency_code
            """))
        {
            Add(journal, "@company", company);
            Add(journal, "@kind", docKind);
            Add(journal, "@doc", docId.ToString(CultureInfo.InvariantCulture));
            using var reader = journal.ExecuteReader();
            while (reader.Read())
            {
                journalCurrency ??= Text(reader.GetValue(2));
                var account = Text(reader.GetValue(0)) ?? "";
                var debit = Dec(reader.GetValue(1));
                if (debit > 0m && !excluded.Contains(account))
                    candidates[account] = candidates.GetValueOrDefault(account) + debit;
            }
        }
        if (journalCurrency is null
            || !string.Equals(journalCurrency, currencyCode.Trim(), StringComparison.OrdinalIgnoreCase)
            || candidates.Count != 1)
            throw Invalid();
        var (accountCode, debitTotal) = candidates.Single();
        if (charge.Amount > debitTotal) throw Invalid();
        return (supplierId, accountCode);
    }

    public async Task<Guid> CreateDraftAsync(
        Guid companyId, IReadOnlyList<PurchaseLandedCostReceiptSelection> receipts, string documentNumber, DateOnly documentDate,
        string currencyCode, decimal exchangeRate,
        IReadOnlyList<PurchaseLandedCostChargeDraft> charges, string actor,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (receipts is null || receipts.Count == 0)
            throw new AppError(ErrorCodes.LandedCostReceiptSelectionInvalid,
                "The landed-cost voucher selects no eligible receipt lines.");
        if (receipts.Any(x => x.ReceiptDocumentId <= 0)
            || receipts.Select(x => x.ReceiptDocumentId).Distinct().Count() != receipts.Count)
            throw new ArgumentException("Receipt selections must be distinct posted receipts.", nameof(receipts));
        if (string.IsNullOrWhiteSpace(documentNumber)) throw new ArgumentException("Document number is required.", nameof(documentNumber));
        if (string.IsNullOrWhiteSpace(currencyCode)) throw new ArgumentException("Currency is required.", nameof(currencyCode));
        if (exchangeRate <= 0m) throw new ArgumentOutOfRangeException(nameof(exchangeRate));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Authenticated actor is required.", nameof(actor));
        if (charges is null || charges.Count == 0) throw new ArgumentException("At least one landed-cost charge is required.", nameof(charges));
        if (charges.Any(x => string.IsNullOrWhiteSpace(x.ChargeCode) || string.IsNullOrWhiteSpace(x.Description)))
            throw new ArgumentException("Every charge requires a code and description.", nameof(charges));
        if (charges.Select(x => x.ChargeCode.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != charges.Count)
            throw new ArgumentException("Charge codes must be unique within a voucher.", nameof(charges));
        if (charges.Any(x => !LandedCostAllocationBases.All.Contains(x.AllocationBasis)))
            throw new ArgumentException("A charge has an unsupported allocation basis.", nameof(charges));
        if (charges.Any(x => !LandedCostCreditModes.All.Contains(x.CreditMode ?? LandedCostCreditModes.Payable)))
            throw new ArgumentException("A charge has an unsupported credit mode.", nameof(charges));
        foreach (var charge in charges)
        {
            var mode = CanonicalCreditMode(charge.CreditMode);
            if (mode == LandedCostCreditModes.Linked)
            {
                // A charge is either Linked or Unlinked — never both, and a
                // Linked charge must name its supplier document.
                if (charge.LinkedDocumentId is null or <= 0)
                    throw new AppError(ErrorCodes.LandedCostLinkedDocumentInvalid,
                        $"Charge '{charge.ChargeCode}' cannot link to supplier document {charge.LinkedDocumentId}.",
                        charge.ChargeCode.Trim(), charge.LinkedDocumentId ?? 0);
                if (charge.Amount <= 0m)
                    throw new AppError(ErrorCodes.LandedCostLinkedDocumentInvalid,
                        $"Charge '{charge.ChargeCode}' cannot link to supplier document {charge.LinkedDocumentId}.",
                        charge.ChargeCode.Trim(), charge.LinkedDocumentId.Value);
            }
            else if (charge.LinkedDocumentId is not null)
                throw new ArgumentException("Only a Linked charge may carry a linked supplier document.", nameof(charges));
        }
        var linkedIds = charges
            .Where(x => CanonicalCreditMode(x.CreditMode) == LandedCostCreditModes.Linked)
            .Select(x => x.LinkedDocumentId!.Value).ToArray();
        if (linkedIds.Distinct().Count() != linkedIds.Length)
            throw new ArgumentException("A supplier document may be linked by only one charge per voucher.", nameof(charges));

        await using var connection = Open();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var company = companyId.ToString("D");
            var fx = FxRates.Resolve(connection, transaction, _provider,
                companyId, currencyCode, documentDate, exchangeRate);
            var effectiveRate = fx.Rate;
            var stockCompanyCode = PartyLedger.ResolveCompanyCode(connection, transaction, _provider, company);

            // Linked charges (Critical-1): resolve each linked supplier
            // document NOW — its supplier becomes the charge payee, and
            // the account its posted journal originally debited becomes
            // the reclass credit account, stored on the charge. A document
            // already consumed by a posted voucher is rejected (PUR-1004).
            var linkedByChargeCode = new Dictionary<string, (long SupplierId, string AccountCode)>(StringComparer.OrdinalIgnoreCase);
            foreach (var charge in charges)
            {
                if (CanonicalCreditMode(charge.CreditMode) != LandedCostCreditModes.Linked) continue;
                linkedByChargeCode[charge.ChargeCode.Trim()] = ResolveLinkedCharge(
                    connection, transaction, company, charge, currencyCode);
            }

            // L1: a named payee must be a real party of this company.
            foreach (var charge in charges)
            {
                if (CanonicalCreditMode(charge.CreditMode) == LandedCostCreditModes.Linked) continue;
                if (charge.PayeePartyId is not long payee) continue;
                using var check = Cmd(connection, transaction, """
                    SELECT COUNT(*) FROM parties
                    WHERE company_id = @company AND ledger_id = @payee AND is_deleted = @false
                    """);
                Add(check, "@company", stockCompanyCode);
                Add(check, "@payee", payee);
                Add(check, "@false", Pg ? false : 0);
                if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                    throw new AppError(ErrorCodes.LandedCostPayeeNotFound,
                        $"Landed-cost charge '{charge.ChargeCode}' names payee party {payee}, which is not an active party of this company.",
                        charge.ChargeCode.Trim(), payee);
            }

            var receiptLines = new List<ReceiptCostLine>();
            long? supplierId = null;
            string? supplierName = null;
            long primaryReceiptId = receipts[0].ReceiptDocumentId;
            foreach (var selection in receipts)
            {
                string receiptNo;
                long? receiptSupplier;
                string? receiptSupplierName;
                using (var receipt = Cmd(connection, transaction, """
                    SELECT doc_no, party_id, party_name
                    FROM purchase_documents
                    WHERE company_id = @company AND doc_id = @receipt
                      AND doc_kind = 'RCV' AND status = 'Posted' AND is_deleted = @false
                    """))
                {
                    Add(receipt, "@company", company);
                    Add(receipt, "@receipt", selection.ReceiptDocumentId);
                    Add(receipt, "@false", Pg ? false : 0);
                    using var reader = receipt.ExecuteReader();
                    if (!reader.Read()) throw new InvalidOperationException("A posted goods receipt in the selected company is required.");
                    receiptNo = Text(reader.GetValue(0)) ?? string.Empty;
                    receiptSupplier = reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                    receiptSupplierName = reader.IsDBNull(2) ? null : Text(reader.GetValue(2));
                }
                if (selection.ReceiptDocumentId == primaryReceiptId)
                {
                    supplierId = receiptSupplier;
                    supplierName = receiptSupplierName;
                }
                IReadOnlySet<long>? included = selection.IncludedLineIds is null
                    ? null : selection.IncludedLineIds.ToHashSet();
                var lines = LoadReceiptCostLines(connection, transaction, stockCompanyCode,
                    selection.ReceiptDocumentId, receiptNo, included);
                if (lines.Count == 0)
                    throw new AppError(ErrorCodes.LandedCostReceiptSelectionInvalid,
                        "The landed-cost voucher selects no eligible receipt lines.");
                receiptLines.AddRange(lines);
            }
            if (supplierId is null or <= 0)
                throw new InvalidOperationException("The selected goods receipt must identify a supplier before a landed-cost voucher can be created.");

            var allocatorLines = receiptLines.Select(x => new LandedCostReceiptLine(
                x.LineId, x.BaseQuantity, x.BaseGoodsAmount, x.Weight, x.Volume)).ToArray();
            var chargeIds = charges.ToDictionary(x => x.ChargeCode.Trim(), _ => Guid.NewGuid(), StringComparer.OrdinalIgnoreCase);
            var landedCostId = Guid.NewGuid();
            var totalAmount = decimal.Round(charges.Sum(x => x.Amount), 4, MidpointRounding.AwayFromZero);
            var totalBase = decimal.Round(charges.Sum(x =>
                decimal.Round(x.Amount * effectiveRate, 4, MidpointRounding.AwayFromZero)), 4, MidpointRounding.AwayFromZero);
            if (totalAmount == 0m || totalBase == 0m)
                throw new InvalidOperationException("The landed-cost voucher must have a non-zero net charge.");

            using (var header = Cmd(connection, transaction, """
                INSERT INTO purchase_landed_costs
                    (landed_cost_id, company_id, doc_no, doc_date, receipt_doc_id,
                     supplier_id, supplier_name, currency_code, exchange_rate,
                     total_amount, total_base_amount, status, created_by)
                VALUES (@id, @company, @number, @date, @receipt, @supplier, @supplierName,
                        @currency, @rate, @amount, @base, 'Draft', @actor)
                """))
            {
                Add(header, "@id", Id(landedCostId));
                Add(header, "@company", company);
                Add(header, "@number", documentNumber.Trim());
                Add(header, "@date", DateValue(documentDate));
                Add(header, "@receipt", primaryReceiptId);
                Add(header, "@supplier", (object?)supplierId ?? DBNull.Value);
                Add(header, "@supplierName", (object?)supplierName ?? DBNull.Value);
                Add(header, "@currency", currencyCode.Trim().ToUpperInvariant());
                Add(header, "@rate", effectiveRate);
                Add(header, "@amount", totalAmount);
                Add(header, "@base", totalBase);
                Add(header, "@actor", actor.Trim());
                header.ExecuteNonQuery();
            }

            foreach (var selection in receipts)
            {
                using var link = Cmd(connection, transaction, """
                    INSERT INTO purchase_landed_cost_receipts (landed_cost_id, receipt_doc_id)
                    VALUES (@landed, @receipt)
                    """);
                Add(link, "@landed", Id(landedCostId));
                Add(link, "@receipt", selection.ReceiptDocumentId);
                link.ExecuteNonQuery();
            }

            foreach (var charge in charges)
            {
                var chargeId = chargeIds[charge.ChargeCode.Trim()];
                var baseAmount = decimal.Round(charge.Amount * effectiveRate, 4, MidpointRounding.AwayFromZero);
                var linked = linkedByChargeCode.GetValueOrDefault(charge.ChargeCode.Trim());
                using (var insert = Cmd(connection, transaction, """
                    INSERT INTO purchase_landed_cost_charges
                        (charge_id, landed_cost_id, charge_code, description, allocation_basis,
                         amount, base_amount, expense_account_code, payee_party_id, credit_mode,
                         linked_document_id, linked_account_code)
                    VALUES (@id, @landed, @code, @description, @basis, @amount, @base, @account, @payee, @mode,
                            @linkedDoc, @linkedAccount)
                    """))
                {
                    Add(insert, "@id", Id(chargeId));
                    Add(insert, "@landed", Id(landedCostId));
                    Add(insert, "@code", charge.ChargeCode.Trim());
                    Add(insert, "@description", charge.Description.Trim());
                    Add(insert, "@basis", charge.AllocationBasis);
                    Add(insert, "@amount", charge.Amount);
                    Add(insert, "@base", baseAmount);
                    Add(insert, "@account", (object?)charge.ExpenseAccountCode?.Trim() ?? DBNull.Value);
                    // A Linked charge's payee IS the linked document's supplier.
                    Add(insert, "@payee", (object?)(linked.SupplierId != 0 ? linked.SupplierId : charge.PayeePartyId) ?? DBNull.Value);
                    Add(insert, "@mode", CanonicalCreditMode(charge.CreditMode));
                    Add(insert, "@linkedDoc", (object?)charge.LinkedDocumentId ?? DBNull.Value);
                    Add(insert, "@linkedAccount", (object?)(linked.AccountCode) ?? DBNull.Value);
                    insert.ExecuteNonQuery();
                }
            }

            var allocations = LandedCostAllocator.Allocate(allocatorLines, charges.Select(x =>
            {
                var baseAmount = decimal.Round(x.Amount * effectiveRate, 4, MidpointRounding.AwayFromZero);
                IReadOnlyDictionary<long, decimal>? manualBaseAmounts = null;
                if (x.ManualAmounts is not null)
                {
                    if (decimal.Round(x.ManualAmounts.Values.Sum(), 4, MidpointRounding.AwayFromZero)
                        != decimal.Round(x.Amount, 4, MidpointRounding.AwayFromZero))
                        throw new InvalidOperationException($"Manual allocations for charge '{x.ChargeCode}' must sum to its entered amount.");
                    var converted = x.ManualAmounts.OrderBy(item => item.Key).ToDictionary(
                        item => item.Key,
                        item => decimal.Round(item.Value * effectiveRate, 4, MidpointRounding.AwayFromZero));
                    if (converted.Count > 0)
                    {
                        var finalKey = converted.Keys.Max();
                        converted[finalKey] = decimal.Round(
                            converted[finalKey] + baseAmount - converted.Values.Sum(),
                            4, MidpointRounding.AwayFromZero);
                    }
                    manualBaseAmounts = converted;
                }
                return new LandedCostCharge(x.ChargeCode.Trim(), baseAmount, x.AllocationBasis, manualBaseAmounts);
            }).ToArray());
            var lineById = receiptLines.ToDictionary(x => x.LineId);
            foreach (var allocation in allocations)
            {
                if (allocation.AllocatedBaseAmount == 0m) continue;
                var chargeId = chargeIds[allocation.ChargeCode];
                var line = lineById[allocation.ReceiptLineId];
                using var insert = Cmd(connection, transaction, """
                    INSERT INTO purchase_landed_cost_allocations
                        (allocation_id, landed_cost_id, charge_id, company_id, receipt_doc_id,
                         receipt_line_id, material_id, warehouse_id, base_quantity,
                         allocated_base_amount, unit_cost_addition)
                    VALUES (@id, @landed, @charge, @company, @receipt, @line,
                            @material, @warehouse, @qty, @amount, @unitCost)
                    """);
                Add(insert, "@id", Id(Guid.NewGuid()));
                Add(insert, "@landed", Id(landedCostId));
                Add(insert, "@charge", Id(chargeId));
                Add(insert, "@company", company);
                Add(insert, "@receipt", line.ReceiptId);
                Add(insert, "@line", line.LineId);
                Add(insert, "@material", line.MaterialId);
                Add(insert, "@warehouse", line.WarehouseId);
                Add(insert, "@qty", line.BaseQuantity);
                Add(insert, "@amount", allocation.AllocatedBaseAmount);
                Add(insert, "@unitCost", decimal.Round(allocation.AllocatedBaseAmount / line.BaseQuantity, 6, MidpointRounding.AwayFromZero));
                insert.ExecuteNonQuery();
            }
            AddEvent(connection, transaction, company, landedCostId, "DraftCreated", actor, null);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return landedCostId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<PurchaseLandedCostSummary>> ListAsync(
        Guid companyId, string? search = null, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<PurchaseLandedCostSummary>();
        await using var command = Cmd(connection, null, """
            SELECT h.landed_cost_id, h.company_id, h.doc_no, h.doc_date,
                   h.receipt_doc_id, d.doc_no, h.supplier_id, h.supplier_name,
                   h.currency_code, h.exchange_rate, h.total_amount, h.total_base_amount,
                   h.status, h.journal_entry_id, h.source_reversal_id
            FROM purchase_landed_costs h
            JOIN purchase_documents d
              ON d.doc_id = h.receipt_doc_id AND d.company_id = h.company_id
            WHERE h.company_id = @company
              AND (@search = '' OR h.doc_no LIKE @pattern OR d.doc_no LIKE @pattern OR h.supplier_name LIKE @pattern)
            ORDER BY h.doc_date DESC, h.created_at DESC
            LIMIT 300
            """);
        Add(command, "@company", companyId.ToString("D"));
        Add(command, "@search", (search ?? "").Trim());
        Add(command, "@pattern", "%" + (search ?? "").Trim() + "%");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid? journal = reader.IsDBNull(13) ? null : Guid.Parse(Text(reader.GetValue(13))!);
            Guid? reversal = reader.IsDBNull(14) ? null : Guid.Parse(Text(reader.GetValue(14))!);
            rows.Add(new PurchaseLandedCostSummary(
                Guid.Parse(Text(reader.GetValue(0))!),
                Guid.Parse(Text(reader.GetValue(1))!),
                Text(reader.GetValue(2)) ?? "",
                DateOnly.Parse(Text(reader.GetValue(3)) ?? "", CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                Text(reader.GetValue(5)) ?? "",
                reader.IsDBNull(6) ? null : Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture),
                reader.IsDBNull(7) ? null : Text(reader.GetValue(7)),
                Text(reader.GetValue(8)) ?? "SAR",
                Dec(reader.GetValue(9)), Dec(reader.GetValue(10)), Dec(reader.GetValue(11)),
                Text(reader.GetValue(12)) ?? "Draft", journal, reversal));
        }
        return rows;
    }

    public async Task<Guid> PostAsync(Guid companyId, Guid landedCostId, string actor,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty || landedCostId == Guid.Empty) throw new ArgumentException("Company and voucher are required.");
        RequireActor(actor);
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journalId = PostCore(connection, transaction, companyId, landedCostId, actor, "LND");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return journalId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Guid> ReverseAsync(Guid companyId, Guid landedCostId, string reversalNumber,
        DateOnly reversalDate, string reason, string actor, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty || landedCostId == Guid.Empty) throw new ArgumentException("Company and voucher are required.");
        if (string.IsNullOrWhiteSpace(reversalNumber) || string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reversal number and reason are required.");
        RequireActor(actor);
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var company = companyId.ToString("D");
            string receiptCurrency;
            decimal rate, totalAmount, totalBase;
            long receiptId;
            long supplierId;
            string supplierName;
            using (var original = Cmd(connection, transaction, $"""
                SELECT receipt_doc_id, supplier_id, supplier_name, currency_code,
                       exchange_rate, total_amount, total_base_amount, status
                FROM purchase_landed_costs
                WHERE company_id = @company AND landed_cost_id = @id
                {(Pg ? "FOR UPDATE" : string.Empty)}
                """))
            {
                Add(original, "@company", company);
                Add(original, "@id", Id(landedCostId));
                using var reader = original.ExecuteReader();
                if (!reader.Read()) throw new InvalidOperationException("The landed-cost voucher was not found in this company.");
                if (!string.Equals(Text(reader.GetValue(7)), "Posted", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only a posted landed-cost voucher can be reversed.");
                receiptId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                supplierId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                supplierName = Text(reader.GetValue(2)) ?? string.Empty;
                receiptCurrency = Text(reader.GetValue(3)) ?? "SAR";
                rate = Dec(reader.GetValue(4));
                totalAmount = -Dec(reader.GetValue(5));
                totalBase = -Dec(reader.GetValue(6));
            }

            var reversalId = Guid.NewGuid();
            using (var header = Cmd(connection, transaction, """
                INSERT INTO purchase_landed_costs
                    (landed_cost_id, company_id, doc_no, doc_date, receipt_doc_id,
                     supplier_id, supplier_name, currency_code, exchange_rate,
                     total_amount, total_base_amount, status, created_by)
                VALUES (@id, @company, @number, @date, @receipt, @supplier, @supplierName,
                        @currency, @rate, @amount, @base, 'Draft', @actor)
                """))
            {
                Add(header, "@id", Id(reversalId));
                Add(header, "@company", company);
                Add(header, "@number", reversalNumber.Trim());
                Add(header, "@date", DateValue(reversalDate));
                Add(header, "@receipt", receiptId);
                Add(header, "@supplier", supplierId);
                Add(header, "@supplierName", supplierName);
                Add(header, "@currency", receiptCurrency);
                Add(header, "@rate", rate);
                Add(header, "@amount", totalAmount);
                Add(header, "@base", totalBase);
                Add(header, "@actor", actor.Trim());
                header.ExecuteNonQuery();
            }

            using (var links = Cmd(connection, transaction, """
                INSERT INTO purchase_landed_cost_receipts (landed_cost_id, receipt_doc_id)
                SELECT @reversal, receipt_doc_id FROM purchase_landed_cost_receipts
                WHERE landed_cost_id = @id
                """))
            {
                Add(links, "@reversal", Id(reversalId));
                Add(links, "@id", Id(landedCostId));
                links.ExecuteNonQuery();
            }

            var originalCharges = new List<(string Code, string Description, string Basis, decimal Amount, decimal Base, object Account, object Payee, string Mode, object LinkedDoc, object LinkedAccount)>();
            using (var charges = Cmd(connection, transaction, """
                SELECT charge_code, description, allocation_basis, amount, base_amount,
                       expense_account_code, payee_party_id, credit_mode,
                       linked_document_id, linked_account_code
                FROM purchase_landed_cost_charges WHERE landed_cost_id = @id
                """))
            {
                Add(charges, "@id", Id(landedCostId));
                using var reader = charges.ExecuteReader();
                while (reader.Read())
                    originalCharges.Add((Text(reader.GetValue(0)) ?? "", Text(reader.GetValue(1)) ?? "",
                        Text(reader.GetValue(2)) ?? "Manual", Dec(reader.GetValue(3)), Dec(reader.GetValue(4)),
                        reader.IsDBNull(5) ? DBNull.Value : reader.GetValue(5),
                        reader.IsDBNull(6) ? DBNull.Value : reader.GetValue(6),
                        Text(reader.GetValue(7)) ?? LandedCostCreditModes.Payable,
                        reader.IsDBNull(8) ? DBNull.Value : reader.GetValue(8),
                        reader.IsDBNull(9) ? DBNull.Value : reader.GetValue(9)));
            }
            var reversalChargeIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in originalCharges)
            {
                var newChargeId = Guid.NewGuid();
                reversalChargeIds[item.Code] = newChargeId;
                using var insert = Cmd(connection, transaction, """
                    INSERT INTO purchase_landed_cost_charges
                        (charge_id, landed_cost_id, charge_code, description, allocation_basis,
                         amount, base_amount, expense_account_code, payee_party_id, credit_mode,
                         linked_document_id, linked_account_code)
                    VALUES (@id, @landed, @code, @description, @basis, @amount, @base, @account, @payee, @mode,
                            @linkedDoc, @linkedAccount)
                    """);
                Add(insert, "@id", Id(newChargeId));
                Add(insert, "@landed", Id(reversalId));
                Add(insert, "@code", item.Code);
                Add(insert, "@description", "Reversal: " + item.Description);
                Add(insert, "@basis", item.Basis);
                Add(insert, "@amount", -item.Amount);
                Add(insert, "@base", -item.Base);
                Add(insert, "@account", item.Account);
                Add(insert, "@payee", item.Payee);
                Add(insert, "@mode", item.Mode);
                Add(insert, "@linkedDoc", item.LinkedDoc);
                Add(insert, "@linkedAccount", item.LinkedAccount);
                insert.ExecuteNonQuery();
            }

            var originalAllocations = new List<(Guid Charge, long Receipt, long Line, long Material, long Warehouse, decimal Qty, decimal Amount, decimal Unit, decimal? Inventory, decimal? Consumed)>();
            using (var allocations = Cmd(connection, transaction, """
                SELECT charge_id, receipt_doc_id, receipt_line_id, material_id, warehouse_id, base_quantity,
                       allocated_base_amount, unit_cost_addition, inventory_amount, consumed_amount
                FROM purchase_landed_cost_allocations WHERE landed_cost_id = @id
                """))
            {
                Add(allocations, "@id", Id(landedCostId));
                using var reader = allocations.ExecuteReader();
                while (reader.Read())
                    originalAllocations.Add((Guid.Parse(Text(reader.GetValue(0))!), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                        Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                        Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture), Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                        Dec(reader.GetValue(5)), Dec(reader.GetValue(6)), Dec(reader.GetValue(7)),
                        reader.IsDBNull(8) ? null : Dec(reader.GetValue(8)),
                        reader.IsDBNull(9) ? null : Dec(reader.GetValue(9))));
            }
            foreach (var item in originalAllocations)
            {
                string code;
                using (var codeQuery = Cmd(connection, transaction,
                    "SELECT charge_code FROM purchase_landed_cost_charges WHERE charge_id = @id"))
                {
                    Add(codeQuery, "@id", Id(item.Charge));
                    code = Convert.ToString(codeQuery.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
                }
                if (!reversalChargeIds.TryGetValue(code, out var newCharge))
                    throw new InvalidOperationException("A reversal allocation has no matching reversal charge.");
                using var insert = Cmd(connection, transaction, """
                    INSERT INTO purchase_landed_cost_allocations
                        (allocation_id, landed_cost_id, charge_id, company_id, receipt_doc_id,
                         receipt_line_id, material_id, warehouse_id, base_quantity,
                         allocated_base_amount, unit_cost_addition, inventory_amount, consumed_amount)
                    VALUES (@id, @landed, @charge, @company, @receipt, @line,
                            @material, @warehouse, @qty, @amount, @unit, @inventory, @consumed)
                    """);
                Add(insert, "@id", Id(Guid.NewGuid()));
                Add(insert, "@landed", Id(reversalId));
                Add(insert, "@charge", Id(newCharge));
                Add(insert, "@company", company);
                Add(insert, "@receipt", item.Receipt);
                Add(insert, "@line", item.Line);
                Add(insert, "@material", item.Material);
                Add(insert, "@warehouse", item.Warehouse);
                Add(insert, "@qty", item.Qty);
                Add(insert, "@amount", -item.Amount);
                Add(insert, "@unit", -item.Unit);
                // L3: the reversal allocation carries the STORED split,
                // negated; PostCore replays it instead of recomputing.
                Add(insert, "@inventory", (object?)(item.Inventory is null ? null : -item.Inventory.Value) ?? DBNull.Value);
                Add(insert, "@consumed", (object?)(item.Consumed is null ? null : -item.Consumed.Value) ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }

            var journalId = PostCore(connection, transaction, companyId, reversalId, actor, "LND-REV");
            using (var originalUpdate = Cmd(connection, transaction,
                "UPDATE purchase_landed_costs SET status = 'Cancelled', source_reversal_id = @reversal WHERE company_id = @company AND landed_cost_id = @id AND status = 'Posted'"))
            {
                Add(originalUpdate, "@company", company);
                Add(originalUpdate, "@id", Id(landedCostId));
                Add(originalUpdate, "@reversal", Id(reversalId));
                if (originalUpdate.ExecuteNonQuery() != 1) throw new InvalidOperationException("The original voucher changed while reversal was being posted.");
            }
            AddEvent(connection, transaction, company, landedCostId, "Reversed", actor, reason.Trim());
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return journalId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private sealed record ChargePosting(
        Guid Id, string Code, decimal Amount, decimal BaseAmount,
        string? ExpenseAccount, long? PayeePartyId, string CreditMode,
        long? LinkedDocumentId, string? LinkedAccountCode);

    private Guid PostCore(DbConnection connection, DbTransaction transaction, Guid companyId,
        Guid landedCostId, string actor, string sourceType)
    {
        var company = companyId.ToString("D");
        DateOnly date;
        string docNo, currency, status;
        long receiptId, supplierId;
        decimal exchangeRate, totalAmount, totalBase;
        using (var header = Cmd(connection, transaction, $"""
            SELECT doc_no, doc_date, receipt_doc_id, supplier_id, currency_code,
                   exchange_rate, total_amount, total_base_amount, status
            FROM purchase_landed_costs
            WHERE company_id = @company AND landed_cost_id = @id
            {(Pg ? "FOR UPDATE" : string.Empty)}
            """))
        {
            Add(header, "@company", company);
            Add(header, "@id", Id(landedCostId));
            using var reader = header.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("The landed-cost voucher was not found in this company.");
            docNo = Text(reader.GetValue(0)) ?? "";
            date = DateOnly.Parse(Text(reader.GetValue(1)) ?? "", CultureInfo.InvariantCulture);
            receiptId = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            supplierId = reader.IsDBNull(3) ? 0 : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
            currency = Text(reader.GetValue(4)) ?? "SAR";
            exchangeRate = Dec(reader.GetValue(5));
            totalAmount = Dec(reader.GetValue(6));
            totalBase = Dec(reader.GetValue(7));
            status = Text(reader.GetValue(8)) ?? "";
        }
        if (status != "Draft") throw new InvalidOperationException("Only a draft landed-cost voucher can be posted.");
        if (supplierId <= 0) throw new InvalidOperationException("The receipt must identify a supplier before landed costs can be posted.");

        var charges = new List<ChargePosting>();
        using (var query = Cmd(connection, transaction, """
            SELECT charge_id, charge_code, amount, base_amount, expense_account_code, payee_party_id, credit_mode,
                   linked_document_id, linked_account_code
            FROM purchase_landed_cost_charges WHERE landed_cost_id = @id
            """))
        {
            Add(query, "@id", Id(landedCostId));
            using var reader = query.ExecuteReader();
            while (reader.Read())
                charges.Add(new ChargePosting(
                    Guid.Parse(Text(reader.GetValue(0))!), Text(reader.GetValue(1)) ?? "",
                    Dec(reader.GetValue(2)), Dec(reader.GetValue(3)),
                    reader.IsDBNull(4) ? null : Text(reader.GetValue(4)),
                    reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                    Text(reader.GetValue(6)) ?? LandedCostCreditModes.Payable,
                    reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
                    reader.IsDBNull(8) ? null : Text(reader.GetValue(8))));
        }
        if (charges.Count == 0) throw new InvalidOperationException("The landed-cost voucher has no charges.");
        foreach (var charge in charges)
        {
            if (string.Equals(charge.CreditMode, LandedCostCreditModes.AlreadyBooked, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(charge.ExpenseAccount))
                throw new AppError(ErrorCodes.LandedCostAlreadyBookedNeedsAccount,
                    $"Landed-cost charge '{charge.Code}' is marked AlreadyBooked but has no expense account.",
                    charge.Code);
            if (string.Equals(charge.CreditMode, LandedCostCreditModes.Linked, StringComparison.OrdinalIgnoreCase))
            {
                if (charge.LinkedDocumentId is null || string.IsNullOrWhiteSpace(charge.LinkedAccountCode))
                    throw new AppError(ErrorCodes.LandedCostLinkedDocumentInvalid,
                        $"Charge '{charge.Code}' cannot link to supplier document {charge.LinkedDocumentId}.",
                        charge.Code, charge.LinkedDocumentId ?? 0);
                // Duplicate prevention is re-checked at posting (another
                // draft may have posted first). Reversal postings skip it:
                // they unwind the original voucher's consumption.
                if (sourceType == "LND")
                {
                    var consuming = FindConsumingVoucher(connection, transaction, company,
                        charge.LinkedDocumentId.Value, landedCostId);
                    if (consuming is not null)
                        throw new AppError(ErrorCodes.LandedCostDocumentAlreadyLinked,
                            $"Supplier document {charge.LinkedDocumentId.Value} is already consumed by posted landed-cost voucher '{consuming}'.",
                            consuming, charge.LinkedDocumentId.Value);
                }
            }
        }

        var stockCompanyCode = PartyLedger.ResolveCompanyCode(connection, transaction, _provider, company);
        var allocations = new List<(Guid Id, long Line, long Material, long Warehouse, decimal Amount, string ExpenseAccount, long Movement, decimal? StoredInventory)>();
        using (var query = Cmd(connection, transaction, $"""
            SELECT a.allocation_id, a.receipt_line_id, a.material_id, a.warehouse_id,
                   a.base_quantity, a.allocated_base_amount, c.expense_account_code,
                   (SELECT COUNT(*) FROM material_stock_movements sm
                    WHERE sm.company_id = @stockCompany
                      AND sm.source_document_line_id = a.receipt_line_id
                      AND sm.document_no = d.doc_no AND sm.movement_type = 'RCV'
                      AND sm.qty_in > 0 AND sm.is_deleted = {(Pg ? "FALSE" : "0")}) AS MovementCount,
                   (SELECT MIN(sm.stock_movement_id) FROM material_stock_movements sm
                    WHERE sm.company_id = @stockCompany
                      AND sm.source_document_line_id = a.receipt_line_id
                      AND sm.document_no = d.doc_no AND sm.movement_type = 'RCV'
                      AND sm.qty_in > 0 AND sm.is_deleted = {(Pg ? "FALSE" : "0")}) AS MovementId,
                   (SELECT MIN(sm.qty_in) FROM material_stock_movements sm
                    WHERE sm.company_id = @stockCompany
                      AND sm.source_document_line_id = a.receipt_line_id
                      AND sm.document_no = d.doc_no AND sm.movement_type = 'RCV'
                      AND sm.qty_in > 0 AND sm.is_deleted = {(Pg ? "FALSE" : "0")}) AS MovementQty,
                   a.inventory_amount
            FROM purchase_landed_cost_allocations a
            JOIN purchase_landed_cost_charges c
              ON c.charge_id = a.charge_id AND c.landed_cost_id = a.landed_cost_id
            JOIN purchase_documents d ON d.doc_id = a.receipt_doc_id AND d.company_id = a.company_id
            WHERE a.company_id = @company AND a.landed_cost_id = @landed
            ORDER BY a.receipt_line_id, a.allocation_id
            """))
        {
            Add(query, "@company", company);
            Add(query, "@stockCompany", stockCompanyCode);
            Add(query, "@landed", Id(landedCostId));
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(3)) throw new InvalidOperationException("A landed-cost allocation has no receipt warehouse.");
                var movementCount = Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture);
                if (movementCount != 1 || reader.IsDBNull(8) || reader.IsDBNull(9))
                    throw new InvalidOperationException("Every landed-cost allocation must map to exactly one active stock movement for its receipt line.");
                var allocatedQty = Dec(reader.GetValue(4));
                var movementQty = Dec(reader.GetValue(9));
                if (decimal.Round(allocatedQty, 6, MidpointRounding.AwayFromZero)
                    != decimal.Round(movementQty, 6, MidpointRounding.AwayFromZero))
                    throw new InvalidOperationException("The landed-cost allocation quantity no longer matches its source receipt movement.");
                var expenseAccount = reader.IsDBNull(6) || string.IsNullOrWhiteSpace(Text(reader.GetValue(6)))
                    ? GlAccounts.Cogs : Text(reader.GetValue(6))!.Trim();
                allocations.Add((Guid.Parse(Text(reader.GetValue(0))!), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture), Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Dec(reader.GetValue(5)), expenseAccount, Convert.ToInt64(reader.GetValue(8), CultureInfo.InvariantCulture),
                    reader.IsDBNull(10) ? null : Dec(reader.GetValue(10))));
            }
        }
        if (allocations.Count == 0) throw new InvalidOperationException("The landed-cost voucher has no traceable stock allocations.");
        var allocatedBaseTotal = decimal.Round(allocations.Sum(x => x.Amount), 4, MidpointRounding.AwayFromZero);
        if (allocatedBaseTotal != decimal.Round(totalBase, 4, MidpointRounding.AwayFromZero))
            throw new InvalidOperationException("Landed-cost line allocations do not reconcile to the voucher base total.");

        decimal inventoryBase = 0m, consumedBase = 0m;
        var consumedByAccountBase = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var allocation in allocations)
        {
            var result = StockValuationEngine.ApplyLandedCostAdjustment(connection, transaction, _provider,
                company, stockCompanyCode, landedCostId, allocation.Id, allocation.Movement,
                allocation.Warehouse, allocation.Material, allocation.Amount, actor,
                allocation.StoredInventory);
            inventoryBase += result.InventoryAdjustment;
            consumedBase += result.ConsumedCostAdjustment;
            consumedByAccountBase[allocation.ExpenseAccount] =
                consumedByAccountBase.GetValueOrDefault(allocation.ExpenseAccount) + result.ConsumedCostAdjustment;
            if (allocation.StoredInventory is null)
            {
                // L3: persist the split computed NOW so a later reversal
                // replays these exact amounts instead of recomputing.
                using var store = Cmd(connection, transaction, """
                    UPDATE purchase_landed_cost_allocations
                    SET inventory_amount = @inventory, consumed_amount = @consumed
                    WHERE allocation_id = @id
                    """);
                Add(store, "@inventory", result.InventoryAdjustment);
                Add(store, "@consumed", result.ConsumedCostAdjustment);
                Add(store, "@id", Id(allocation.Id));
                store.ExecuteNonQuery();
            }
        }
        inventoryBase = decimal.Round(inventoryBase, 4, MidpointRounding.AwayFromZero);
        consumedBase = decimal.Round(consumedBase, 4, MidpointRounding.AwayFromZero);
        var inventoryAmount = decimal.Round(inventoryBase / exchangeRate, 4, MidpointRounding.AwayFromZero);
        var consumedAmount = decimal.Round(consumedBase / exchangeRate, 4, MidpointRounding.AwayFromZero);
        var roundingDelta = decimal.Round(totalAmount - inventoryAmount - consumedAmount, 4, MidpointRounding.AwayFromZero);
        if (consumedAmount != 0m) consumedAmount += roundingDelta;
        else inventoryAmount += roundingDelta;

        var consumedByAccount = consumedByAccountBase
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Key,
                item => decimal.Round(item.Value / exchangeRate, 4, MidpointRounding.AwayFromZero),
                StringComparer.OrdinalIgnoreCase);
        if (consumedByAccount.Count > 0)
        {
            var finalAccount = consumedByAccount.Keys.Last();
            consumedByAccount[finalAccount] += consumedAmount - consumedByAccount.Values.Sum();
        }
        else if (consumedAmount != 0m)
        {
            consumedByAccount[GlAccounts.Cogs] = consumedAmount;
        }

        // Credit side (L1/L2): Payable charges credit AP control in total
        // (the party split lives in the subledger below). Linked charges
        // credit the account their supplier document originally debited
        // (a reclass — the liability already exists on that document, so
        // NO new payable and NO party entry). Legacy AlreadyBooked
        // charges credit their own expense account the same way.
        var alreadyBooked = charges
            .Where(x => string.Equals(x.CreditMode, LandedCostCreditModes.AlreadyBooked, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var alreadyBookedByAccount = alreadyBooked
            .GroupBy(x => x.ExpenseAccount!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => decimal.Round(g.Sum(x => x.Amount), 4, MidpointRounding.AwayFromZero),
                StringComparer.OrdinalIgnoreCase);
        var linked = charges
            .Where(x => string.Equals(x.CreditMode, LandedCostCreditModes.Linked, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var linkedByAccount = linked
            .GroupBy(x => x.LinkedAccountCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => decimal.Round(g.Sum(x => x.Amount), 4, MidpointRounding.AwayFromZero),
                StringComparer.OrdinalIgnoreCase);
        var creditAccountTotal = decimal.Round(
            alreadyBookedByAccount.Values.Sum() + linkedByAccount.Values.Sum(), 4, MidpointRounding.AwayFromZero);
        var payableTotal = decimal.Round(totalAmount - creditAccountTotal, 4, MidpointRounding.AwayFromZero);
        var creditAccountBaseTotal = decimal.Round(
            alreadyBooked.Sum(x => x.BaseAmount) + linked.Sum(x => x.BaseAmount), 4, MidpointRounding.AwayFromZero);
        var payableBaseTotal = decimal.Round(totalBase - creditAccountBaseTotal, 4, MidpointRounding.AwayFromZero);

        var glLines = new List<GlLineRequest>();
        AddSignedLine(glLines, GlAccounts.Inventory, inventoryAmount, "Landed cost capitalized to stock");
        foreach (var item in consumedByAccount)
            AddSignedLine(glLines, item.Key, item.Value, "Landed cost on consumed stock");
        foreach (var item in alreadyBookedByAccount)
            AddSignedLine(glLines, item.Key, -item.Value, "Landed cost already booked");
        foreach (var item in linkedByAccount)
            AddSignedLine(glLines, item.Key, -item.Value, "Landed cost reclassed from linked supplier document");
        AddSignedLine(glLines, GlAccounts.Payable, -payableTotal, "Supplier landed-cost charge");
        var gl = GlPosting.PostInTransaction(connection, transaction, _provider,
            new GlDocumentRequest(companyId, date, $"Landed cost {docNo}",
                sourceType, landedCostId.ToString("D"), docNo, currency, exchangeRate, glLines),
            actor);

        // Party subledger (L1): one entry per effective payee across the
        // Payable charges only; Linked (and legacy AlreadyBooked) charges
        // write NO party entry — their liability already exists elsewhere.
        var payableCharges = charges
            .Where(x => string.Equals(x.CreditMode, LandedCostCreditModes.Payable, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var payeeGroups = payableCharges
            .GroupBy(x => x.PayeePartyId ?? supplierId)
            .Select(g => (Payee: g.Key,
                Amount: decimal.Round(g.Sum(x => x.Amount), 4, MidpointRounding.AwayFromZero),
                Base: decimal.Round(g.Sum(x => x.BaseAmount), 4, MidpointRounding.AwayFromZero)))
            .OrderBy(g => g.Payee)
            .ToList();
        for (var i = 0; i < payeeGroups.Count; i++)
        {
            var group = payeeGroups[i];
            var amount = group.Amount;
            var baseAmount = group.Base;
            if (i == payeeGroups.Count - 1)
            {
                amount += payableTotal - payeeGroups.Sum(g => g.Amount);
                baseAmount += payableBaseTotal - payeeGroups.Sum(g => g.Base);
            }
            if (amount == 0m && baseAmount == 0m) continue;
            var kind = ResolvePartyKind(connection, transaction, stockCompanyCode, group.Payee);
            var debit = amount < 0m ? -amount : 0m;
            var credit = amount > 0m ? amount : 0m;
            var baseDebit = baseAmount < 0m ? -baseAmount : 0m;
            var baseCredit = baseAmount > 0m ? baseAmount : 0m;
            PartyLedger.WriteEntry(connection, transaction, _provider, stockCompanyCode, kind, group.Payee,
                sourceType, null, docNo, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                debit, credit, currency, $"Landed cost voucher {docNo}",
                gl.JournalEntryId.ToString("D"), exchangeRate, baseDebit, baseCredit);
        }
        using (var update = Cmd(connection, transaction, """
            UPDATE purchase_landed_costs
            SET status = 'Posted', journal_entry_id = @journal,
                posted_by = @actor, posted_at = CURRENT_TIMESTAMP
            WHERE company_id = @company AND landed_cost_id = @id AND status = 'Draft'
            """))
        {
            Add(update, "@journal", Id(gl.JournalEntryId));
            Add(update, "@actor", actor.Trim());
            Add(update, "@company", company);
            Add(update, "@id", Id(landedCostId));
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("The landed-cost voucher could not be posted.");
        }
        AddEvent(connection, transaction, company, landedCostId, sourceType == "LND-REV" ? "ReversalPosted" : "Posted", actor, null);
        return gl.JournalEntryId;
    }

    private string ResolvePartyKind(DbConnection connection, DbTransaction transaction, string companyCode, long partyId)
    {
        using var command = Cmd(connection, transaction, """
            SELECT kind FROM parties WHERE company_id = @company AND ledger_id = @party AND is_deleted = @false
            """);
        Add(command, "@company", companyCode);
        Add(command, "@party", partyId);
        Add(command, "@false", Pg ? false : 0);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? "S" : (Text(value) ?? "S").Trim().ToUpperInvariant();
    }

    private List<ReceiptCostLine> LoadReceiptCostLines(DbConnection connection, DbTransaction transaction,
        string company, long receiptId, string receiptNo, IReadOnlySet<long>? includedLineIds = null)
    {
        var lines = new List<ReceiptCostLine>();
        using var command = Cmd(connection, transaction, $"""
            SELECT l.line_id, l.material_id, sm.warehouse_id, sm.qty_in, sm.cost_amount,
                   COALESCE(m.weight_net, 0), COALESCE(m.volume, 0), COUNT(sm.stock_movement_id)
            FROM purchase_document_lines l
            JOIN material_stock_movements sm
              ON sm.company_id = @company AND sm.source_document_line_id = l.line_id
             AND sm.document_no = @receiptNo AND sm.movement_type = 'RCV'
             AND sm.qty_in > 0 AND sm.is_deleted = {(Pg ? "FALSE" : "0")}
            LEFT JOIN materials m ON m.company_id = @company AND m.material_id = l.material_id
            WHERE l.doc_id = @receipt
            GROUP BY l.line_id, l.material_id, sm.warehouse_id, sm.qty_in, sm.cost_amount, m.weight_net, m.volume
            ORDER BY l.line_id
            """);
        Add(command, "@company", company);
        Add(command, "@receiptNo", receiptNo);
        Add(command, "@receipt", receiptId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("A receipt line must map to exactly one stock movement before landed costs can be allocated.");
            var lineId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (includedLineIds is not null && !includedLineIds.Contains(lineId)) continue;
            var quantity = Dec(reader.GetValue(3));
            var value = Math.Abs(Dec(reader.GetValue(4)));
            var weightEach = Dec(reader.GetValue(5));
            var volumeEach = Dec(reader.GetValue(6));
            lines.Add(new ReceiptCostLine(receiptId, lineId,
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                quantity, value, weightEach * quantity, volumeEach * quantity));
        }
        return lines;
    }

    private sealed record ReceiptCostLine(long ReceiptId, long LineId, long MaterialId, long WarehouseId,
        decimal BaseQuantity, decimal BaseGoodsAmount, decimal Weight, decimal Volume);

    private void AddEvent(DbConnection connection, DbTransaction transaction, string company,
        Guid landedCostId, string action, string actor, string? reason)
    {
        using var command = Cmd(connection, transaction, """
            INSERT INTO purchase_landed_cost_events
                (event_id, company_id, landed_cost_id, action, actor, reason)
            VALUES (@id, @company, @landed, @action, @actor, @reason)
            """);
        Add(command, "@id", Id(Guid.NewGuid()));
        Add(command, "@company", company);
        Add(command, "@landed", Id(landedCostId));
        Add(command, "@action", action);
        Add(command, "@actor", actor.Trim());
        Add(command, "@reason", (object?)reason ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string CanonicalCreditMode(string? creditMode)
        => string.Equals(creditMode, LandedCostCreditModes.Linked, StringComparison.OrdinalIgnoreCase)
            ? LandedCostCreditModes.Linked
            : LandedCostCreditModes.Payable;

    private static void AddSignedLine(List<GlLineRequest> lines, string account, decimal signedAmount, string note)
    {
        signedAmount = decimal.Round(signedAmount, 4, MidpointRounding.AwayFromZero);
        if (signedAmount > 0m) lines.Add(new GlLineRequest(account, signedAmount, 0m, Note: note));
        else if (signedAmount < 0m) lines.Add(new GlLineRequest(account, 0m, -signedAmount, Note: note));
    }

    private static void RequireActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Authenticated actor is required.", nameof(actor));
    }

    private object Id(Guid id) => Pg ? id : id.ToString("D");
    private object DateValue(DateOnly date) => Pg ? date : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private DbConnection Open() => Pg ? new NpgsqlConnection(_connectionString) : new SqliteConnection(_connectionString);
    private static DbCommand Cmd(DbConnection connection, DbTransaction? transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    private static void Add(DbCommand command, string name, object value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
    private static string? Text(object value) => value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    private static decimal Dec(object value) => value is DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
}
