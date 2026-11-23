using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Transactional landed-cost vouchers. Draft allocations are based on the
/// exact receipt stock movements; posting revalues the matching cost layers,
/// recognizes the consumed portion in COGS, and posts AP/GL atomically.
/// </summary>
public sealed class PurchaseLandedCostRepository
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

    public async Task<Guid> CreateDraftAsync(
        Guid companyId, long receiptDocumentId, string documentNumber, DateOnly documentDate,
        string currencyCode, decimal exchangeRate,
        IReadOnlyList<PurchaseLandedCostChargeDraft> charges, string actor,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiptDocumentId);
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

        await using var connection = Open();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var company = companyId.ToString("D");
            string receiptNo;
            long? supplierId;
            string? supplierName;
            using (var receipt = Cmd(connection, transaction, """
                SELECT doc_no, party_id, party_name
                FROM purchase_documents
                WHERE company_id = @company AND doc_id = @receipt
                  AND doc_kind = 'RCV' AND status = 'Posted' AND is_deleted = @false
                """))
            {
                Add(receipt, "@company", company);
                Add(receipt, "@receipt", receiptDocumentId);
                Add(receipt, "@false", Pg ? false : 0);
                using var reader = receipt.ExecuteReader();
                if (!reader.Read()) throw new InvalidOperationException("A posted goods receipt in the selected company is required.");
                receiptNo = Text(reader.GetValue(0)) ?? string.Empty;
                supplierId = reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                supplierName = reader.IsDBNull(2) ? null : Text(reader.GetValue(2));
            }

            var receiptLines = LoadReceiptCostLines(connection, transaction, company, receiptDocumentId, receiptNo);
            if (receiptLines.Count == 0)
                throw new InvalidOperationException("The selected receipt has no stock-valued material lines eligible for landed costs.");

            var allocatorLines = receiptLines.Select(x => new LandedCostReceiptLine(
                x.LineId, x.BaseQuantity, x.BaseGoodsAmount, x.Weight, x.Volume)).ToArray();
            var chargeIds = charges.ToDictionary(x => x.ChargeCode.Trim(), _ => Guid.NewGuid(), StringComparer.OrdinalIgnoreCase);
            var landedCostId = Guid.NewGuid();
            var totalAmount = decimal.Round(charges.Sum(x => x.Amount), 4, MidpointRounding.AwayFromZero);
            var totalBase = decimal.Round(charges.Sum(x => x.Amount * exchangeRate), 4, MidpointRounding.AwayFromZero);

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
                Add(header, "@receipt", receiptDocumentId);
                Add(header, "@supplier", (object?)supplierId ?? DBNull.Value);
                Add(header, "@supplierName", (object?)supplierName ?? DBNull.Value);
                Add(header, "@currency", currencyCode.Trim().ToUpperInvariant());
                Add(header, "@rate", exchangeRate);
                Add(header, "@amount", totalAmount);
                Add(header, "@base", totalBase);
                Add(header, "@actor", actor.Trim());
                header.ExecuteNonQuery();
            }

            foreach (var charge in charges)
            {
                var chargeId = chargeIds[charge.ChargeCode.Trim()];
                var baseAmount = decimal.Round(charge.Amount * exchangeRate, 4, MidpointRounding.AwayFromZero);
                using (var insert = Cmd(connection, transaction, """
                    INSERT INTO purchase_landed_cost_charges
                        (charge_id, landed_cost_id, charge_code, description, allocation_basis,
                         amount, base_amount, expense_account_code)
                    VALUES (@id, @landed, @code, @description, @basis, @amount, @base, @account)
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
                    insert.ExecuteNonQuery();
                }
            }

            var allocations = LandedCostAllocator.Allocate(allocatorLines, charges.Select(x =>
                new LandedCostCharge(x.ChargeCode.Trim(),
                    decimal.Round(x.Amount * exchangeRate, 4, MidpointRounding.AwayFromZero),
                    x.AllocationBasis, x.ManualAmounts)).ToArray());
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
                Add(insert, "@receipt", receiptDocumentId);
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
            var journalId = PostCore(connection, transaction, companyId, landedCostId, actor, "LND", cancellationToken);
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
            using (var original = Cmd(connection, transaction, """
                SELECT receipt_doc_id, supplier_id, supplier_name, currency_code,
                       exchange_rate, total_amount, total_base_amount, status
                FROM purchase_landed_costs
                WHERE company_id = @company AND landed_cost_id = @id
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
                     total_amount, total_base_amount, status, source_reversal_id, created_by)
                VALUES (@id, @company, @number, @date, @receipt, @supplier, @supplierName,
                        @currency, @rate, @amount, @base, 'Draft', @source, @actor)
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
                Add(header, "@source", Id(landedCostId));
                Add(header, "@actor", actor.Trim());
                header.ExecuteNonQuery();
            }

            var originalCharges = new List<(string Code, string Description, string Basis, decimal Amount, decimal Base, object Account)>();
            using (var charges = Cmd(connection, transaction, """
                SELECT charge_code, description, allocation_basis, amount, base_amount, expense_account_code
                FROM purchase_landed_cost_charges WHERE landed_cost_id = @id
                """))
            {
                Add(charges, "@id", Id(landedCostId));
                using var reader = charges.ExecuteReader();
                while (reader.Read())
                    originalCharges.Add((Text(reader.GetValue(0)) ?? "", Text(reader.GetValue(1)) ?? "",
                        Text(reader.GetValue(2)) ?? "Manual", Dec(reader.GetValue(3)), Dec(reader.GetValue(4)),
                        reader.IsDBNull(5) ? DBNull.Value : reader.GetValue(5)));
            }
            var reversalChargeIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in originalCharges)
            {
                var newChargeId = Guid.NewGuid();
                reversalChargeIds[item.Code] = newChargeId;
                using var insert = Cmd(connection, transaction, """
                    INSERT INTO purchase_landed_cost_charges
                        (charge_id, landed_cost_id, charge_code, description, allocation_basis,
                         amount, base_amount, expense_account_code)
                    VALUES (@id, @landed, @code, @description, @basis, @amount, @base, @account)
                    """);
                Add(insert, "@id", Id(newChargeId));
                Add(insert, "@landed", Id(reversalId));
                Add(insert, "@code", item.Code);
                Add(insert, "@description", "Reversal: " + item.Description);
                Add(insert, "@basis", item.Basis);
                Add(insert, "@amount", -item.Amount);
                Add(insert, "@base", -item.Base);
                Add(insert, "@account", item.Account);
                insert.ExecuteNonQuery();
            }

            var originalAllocations = new List<(Guid Charge, long Line, long Material, long Warehouse, decimal Qty, decimal Amount, decimal Unit)>();
            using (var allocations = Cmd(connection, transaction, """
                SELECT charge_id, receipt_line_id, material_id, warehouse_id, base_quantity,
                       allocated_base_amount, unit_cost_addition
                FROM purchase_landed_cost_allocations WHERE landed_cost_id = @id
                """))
            {
                Add(allocations, "@id", Id(landedCostId));
                using var reader = allocations.ExecuteReader();
                while (reader.Read())
                    originalAllocations.Add((Guid.Parse(Text(reader.GetValue(0))!), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                        Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture), Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                        Dec(reader.GetValue(4)), Dec(reader.GetValue(5)), Dec(reader.GetValue(6))));
            }
            using (var originalChargeCodes = Cmd(connection, transaction, """
                SELECT charge_id, charge_code FROM purchase_landed_cost_charges WHERE landed_cost_id = @id
                """))
            {
                Add(originalChargeCodes, "@id", Id(landedCostId));
                using var reader = originalChargeCodes.ExecuteReader();
                while (reader.Read())
                {
                    var oldChargeId = Guid.Parse(Text(reader.GetValue(0))!);
                    var code = Text(reader.GetValue(1)) ?? "";
                    if (!reversalChargeIds.ContainsKey(code))
                        throw new InvalidOperationException("A landed-cost reversal charge could not be mapped.");
                }
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
                         allocated_base_amount, unit_cost_addition)
                    VALUES (@id, @landed, @charge, @company, @receipt, @line,
                            @material, @warehouse, @qty, @amount, @unit)
                    """);
                Add(insert, "@id", Id(Guid.NewGuid()));
                Add(insert, "@landed", Id(reversalId));
                Add(insert, "@charge", Id(newCharge));
                Add(insert, "@company", company);
                Add(insert, "@receipt", receiptId);
                Add(insert, "@line", item.Line);
                Add(insert, "@material", item.Material);
                Add(insert, "@warehouse", item.Warehouse);
                Add(insert, "@qty", item.Qty);
                Add(insert, "@amount", -item.Amount);
                Add(insert, "@unit", -item.Unit);
                insert.ExecuteNonQuery();
            }

            var journalId = PostCore(connection, transaction, companyId, reversalId, actor, "LND-REV", cancellationToken);
            using (var originalUpdate = Cmd(connection, transaction,
                "UPDATE purchase_landed_costs SET status = 'Cancelled' WHERE company_id = @company AND landed_cost_id = @id AND status = 'Posted'"))
            {
                Add(originalUpdate, "@company", company);
                Add(originalUpdate, "@id", Id(landedCostId));
                if (originalUpdate.ExecuteNonQuery() != 1) throw new InvalidOperationException("The original voucher changed while reversal was being posted.");
            }
            AddEvent(connection, transaction, company, landedCostId, "Reversed", actor, reason.Trim());
            AddEvent(connection, transaction, company, reversalId, "ReversalPosted", actor, $"Reverses {landedCostId:D}; {reason.Trim()}");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return journalId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private Guid PostCore(DbConnection connection, DbTransaction transaction, Guid companyId,
        Guid landedCostId, string actor, string sourceType, CancellationToken cancellationToken)
    {
        var company = companyId.ToString("D");
        DateOnly date;
        string docNo, currency, status;
        long receiptId, supplierId;
        decimal exchangeRate, totalAmount, totalBase;
        using (var header = Cmd(connection, transaction, """
            SELECT doc_no, doc_date, receipt_doc_id, supplier_id, currency_code,
                   exchange_rate, total_amount, total_base_amount, status
            FROM purchase_landed_costs
            WHERE company_id = @company AND landed_cost_id = @id
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

        var allocations = new List<(Guid Id, long Line, long Material, long Warehouse, decimal Amount, long Movement)>();
        using (var query = Cmd(connection, transaction, """
            SELECT a.allocation_id, a.receipt_line_id, a.material_id, a.warehouse_id,
                   a.allocated_base_amount, sm.stock_movement_id
            FROM purchase_landed_cost_allocations a
            JOIN purchase_documents d ON d.doc_id = a.receipt_doc_id AND d.company_id = a.company_id
            JOIN material_stock_movements sm
              ON sm.company_id = a.company_id
             AND sm.source_document_line_id = a.receipt_line_id
             AND sm.document_no = d.doc_no AND sm.movement_type = 'RCV'
             AND sm.qty_in > 0 AND sm.is_deleted = @false
            WHERE a.company_id = @company AND a.landed_cost_id = @landed
            ORDER BY a.receipt_line_id, a.allocation_id
            """))
        {
            Add(query, "@company", company);
            Add(query, "@landed", Id(landedCostId));
            Add(query, "@false", Pg ? false : 0);
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(3)) throw new InvalidOperationException("A landed-cost allocation has no receipt warehouse.");
                allocations.Add((Guid.Parse(Text(reader.GetValue(0))!), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture), Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Dec(reader.GetValue(4)), Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture)));
            }
        }
        if (allocations.Count == 0) throw new InvalidOperationException("The landed-cost voucher has no traceable stock allocations.");

        decimal inventoryBase = 0m, consumedBase = 0m;
        foreach (var allocation in allocations)
        {
            var result = StockValuationEngine.ApplyLandedCostAdjustment(connection, transaction, _provider,
                company, landedCostId, allocation.Id, allocation.Movement, allocation.Warehouse,
                allocation.Material, allocation.Amount, actor);
            inventoryBase += result.InventoryAdjustment;
            consumedBase += result.ConsumedCostAdjustment;
        }
        inventoryBase = decimal.Round(inventoryBase, 4, MidpointRounding.AwayFromZero);
        consumedBase = decimal.Round(consumedBase, 4, MidpointRounding.AwayFromZero);
        var inventoryAmount = decimal.Round(inventoryBase / exchangeRate, 4, MidpointRounding.AwayFromZero);
        var consumedAmount = decimal.Round(consumedBase / exchangeRate, 4, MidpointRounding.AwayFromZero);
        var roundingDelta = decimal.Round(totalAmount - inventoryAmount - consumedAmount, 4, MidpointRounding.AwayFromZero);
        if (consumedAmount != 0m) consumedAmount += roundingDelta;
        else inventoryAmount += roundingDelta;

        var glLines = new List<GlLineRequest>();
        AddSignedLine(glLines, GlAccounts.Inventory, inventoryAmount, "Landed cost capitalized to stock");
        AddSignedLine(glLines, GlAccounts.Cogs, consumedAmount, "Landed cost on consumed stock");
        AddSignedLine(glLines, GlAccounts.Payable, -totalAmount, "Supplier landed-cost charge");
        var gl = GlPosting.PostInTransaction(connection, transaction, _provider,
            new GlDocumentRequest(companyId, date, $"Landed cost {docNo}",
                sourceType, landedCostId.ToString("D"), docNo, currency, exchangeRate, glLines),
            actor);
        var companyCode = PartyLedger.ResolveCompanyCode(connection, transaction, _provider, company);
        var debit = totalAmount < 0m ? -totalAmount : 0m;
        var credit = totalAmount > 0m ? totalAmount : 0m;
        var baseDebit = totalBase < 0m ? -totalBase : 0m;
        var baseCredit = totalBase > 0m ? totalBase : 0m;
        PartyLedger.WriteEntry(connection, transaction, _provider, companyCode, "S", supplierId,
            sourceType, null, docNo, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            debit, credit, currency, $"Landed cost voucher {docNo}",
            gl.JournalEntryId.ToString("D"), exchangeRate, baseDebit, baseCredit);
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

    private List<ReceiptCostLine> LoadReceiptCostLines(DbConnection connection, DbTransaction transaction,
        string company, long receiptId, string receiptNo)
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
            var quantity = Dec(reader.GetValue(3));
            var value = Math.Abs(Dec(reader.GetValue(4)));
            var weightEach = Dec(reader.GetValue(5));
            var volumeEach = Dec(reader.GetValue(6));
            lines.Add(new ReceiptCostLine(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                quantity, value, weightEach * quantity, volumeEach * quantity));
        }
        return lines;
    }

    private sealed record ReceiptCostLine(long LineId, long MaterialId, long WarehouseId,
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
    private static DbCommand Cmd(DbConnection connection, DbTransaction transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    private static void Add(DbCommand command, string name, object value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
    private static string? Text(object value) => value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    private static decimal Dec(object value) => value is DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
}
