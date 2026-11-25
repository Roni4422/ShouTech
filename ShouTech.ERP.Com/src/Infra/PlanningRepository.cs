using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// MRP-lite planning (migration 058), provider-neutral like the other
/// manufacturing repositories. All planning math is in BASE units (the
/// unit stock is kept in); planned orders and suggestions therefore
/// carry the material's base unit.
///
/// Demand: (1) open sales orders — sales_documents doc_kind 'SO' in
/// status Posted (the commercial world keys documents by the company
/// Guid, resolved from the companies registry by code, the CRM-057
/// pattern); (2) reorder points — materials whose total on-hand is
/// below min_qty, demanding max(reorder_qty, min_qty - on-hand).
/// Netting: a demanded material WITH an active BOM becomes a planned
/// production order for max(0, demand - on-hand); its explosion adds
/// gross requirements for its components, which are processed the same
/// way (multi-level, delta-netted per material). A demanded material
/// WITHOUT a BOM becomes a purchase suggestion for
/// max(0, demand - on-hand - incoming purchase orders). Incoming =
/// purchase_documents doc_kind 'PO' in status Posted.
/// </summary>
public sealed class PlanningRepository : IPlanningRepository
{
    private readonly string _provider;
    private readonly string _connectionString;
    private readonly BomRepository _boms;
    private readonly MfgOrderRepository _orders;
    private readonly OrderDocumentRepository _documents;

    public PlanningRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
        _boms = new BomRepository(provider, connectionString);
        _orders = new MfgOrderRepository(provider, connectionString);
        _documents = new OrderDocumentRepository(provider, connectionString);
    }

    private string NotDeleted => _provider == "PostgreSQL" ? "is_deleted = false" : "is_deleted = 0";
    private string NowExpr => _provider == "PostgreSQL" ? "now()" : "datetime('now')";

    // ------------------------------------------------------------------
    // Run
    // ------------------------------------------------------------------
    public async Task<PlanningRun> RunPlanningAsync(string companyId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var company = companyId.Trim();
        var companyGuid = await ResolveCompanyGuidAsync(company, cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var onHand = await LoadOnHandAsync(connection, company, cancellationToken).ConfigureAwait(false);
        var incoming = await LoadIncomingAsync(connection, companyGuid, cancellationToken).ConfigureAwait(false);
        var materialNames = await LoadMaterialNamesAsync(connection, company, cancellationToken).ConfigureAwait(false);
        var baseUnits = await LoadBaseUnitsAsync(connection, company, cancellationToken).ConfigureAwait(false);

        // ---- Demand aggregation (base units) --------------------------
        var demand = new Dictionary<string, (decimal Qty, string Source, string? Due)>(StringComparer.OrdinalIgnoreCase);
        void AddDemand(string code, decimal baseQty, string source, string? due)
        {
            if (baseQty <= 0) return;
            if (demand.TryGetValue(code, out var existing))
                demand[code] = (existing.Qty + baseQty, existing.Source,
                    string.CompareOrdinal(existing.Due ?? "9999", due ?? "9999") < 0 ? existing.Due : due);
            else
                demand[code] = (baseQty, source, due);
        }

        // (1) Open sales orders.
        await using (var so = CreateCommand(connection, null,
            "SELECT l.material_code, l.qty, l.unit, d.delivery_date FROM sales_documents d JOIN sales_document_lines l ON l.doc_id = d.doc_id WHERE d.company_id = @company AND d.doc_kind = 'SO' AND d.status = 'Posted' AND d.is_deleted = 0 AND l.material_code IS NOT NULL"))
        {
            Add(so, "@company", companyGuid.ToString("D"));
            await using var reader = await so.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<(string Code, decimal Qty, string? Unit, string? Due)>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add((ReadString(reader, 0)!, ReadDecimal(reader, 1), ReadString(reader, 2), ReadString(reader, 3)));
            foreach (var row in rows)
            {
                var materialId = await TryResolveMaterialIdAsync(connection, company, row.Code, cancellationToken).ConfigureAwait(false);
                if (materialId is null) continue;
                var equivalent = await UnitEquivalents.ResolveAsync(connection, null, company, materialId.Value, row.Unit, cancellationToken).ConfigureAwait(false);
                AddDemand(row.Code.Trim(), row.Qty * equivalent, "SalesOrder", row.Due);
            }
        }

        // (2) Reorder points.
        await using (var rp = CreateCommand(connection, null,
            $"SELECT code, min_qty, reorder_qty FROM materials WHERE company_id = @company AND min_qty > 0 AND {NotDeleted}"))
        {
            Add(rp, "@company", company);
            await using var reader = await rp.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<(string Code, decimal Min, decimal Reorder)>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add((ReadString(reader, 0)!, ReadDecimal(reader, 1), ReadDecimal(reader, 2)));
            foreach (var row in rows)
            {
                var hand = onHand.GetValueOrDefault(row.Code.Trim());
                if (hand >= row.Min) continue;
                AddDemand(row.Code.Trim(), Math.Max(row.Reorder, row.Min - hand), "ReorderPoint", null);
            }
        }

        // ---- Netting + explosion (delta processing per material) ------
        var run = new PlanningRun
        {
            RunDate = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Status = "Completed"
        };
        var processedGross = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var pending = new Dictionary<string, (decimal Qty, string Source, string? Due)>(demand, StringComparer.OrdinalIgnoreCase);
        var guard = 0;
        while (pending.Count > 0)
        {
            if (++guard > 10000)
                throw new InvalidOperationException("Planning did not converge (requirement graph too deep).");
            var entry = pending.First();
            pending.Remove(entry.Key);
            var code = entry.Key;
            var already = processedGross.GetValueOrDefault(code);
            var delta = entry.Value.Qty; // pending carries only unprocessed deltas
            if (delta <= 0) continue;
            processedGross[code] = already + delta;

            var bom = await _boms.GetDefaultForMaterialAsync(company, code, cancellationToken).ConfigureAwait(false);
            var hand = onHand.GetValueOrDefault(code);
            if (bom is not null)
            {
                // Net only ONCE per material: the first processing takes
                // stock into account; later deltas are fully additional.
                var nettable = already == 0 ? hand : 0m;
                var produceQty = delta - Math.Min(delta, nettable);
                // Stock consumed by this netting is not available again.
                if (already == 0 && hand > 0)
                    onHand[code] = hand - Math.Min(delta, hand);
                if (produceQty <= 0) continue;
                run.PlannedOrders.Add(new PlannedOrder
                {
                    RunId = run.Id,
                    MaterialCode = code,
                    MaterialName = materialNames.GetValueOrDefault(code),
                    Qty = decimal.Round(produceQty, 4),
                    UnitCode = baseUnits.GetValueOrDefault(code) ?? "PCS",
                    BomId = bom.Id,
                    DueDate = entry.Value.Due,
                    Source = entry.Value.Source,
                    Status = PlannedOrderStatuses.Planned
                });
                // Explode in the BOM's output unit.
                var materialId = await _boms.ResolveMaterialIdAsync(connection, null, company, code, cancellationToken).ConfigureAwait(false);
                var outputEquivalent = await UnitEquivalents.ResolveAsync(connection, null, company, materialId, bom.OutputUnit, cancellationToken).ConfigureAwait(false);
                var outputQty = outputEquivalent == 0m ? produceQty : produceQty / outputEquivalent;
                var explosion = await _boms.ExplodeAsync(company, bom.Id, outputQty, cancellationToken).ConfigureAwait(false);
                foreach (var req in explosion.Requirements)
                {
                    var reqCode = req.MaterialCode.Trim();
                    if (pending.TryGetValue(reqCode, out var p))
                        pending[reqCode] = (p.Qty + req.BaseQty, p.Source, p.Due);
                    else
                        pending[reqCode] = (req.BaseQty, "ComponentShortage", entry.Value.Due);
                }
            }
            else
            {
                var nettableStock = already == 0 ? hand : 0m;
                var nettableIncoming = already == 0 ? incoming.GetValueOrDefault(code) : 0m;
                var need = delta - Math.Min(delta, nettableStock + nettableIncoming);
                if (already == 0)
                {
                    var stockUsed = Math.Min(delta, hand);
                    onHand[code] = hand - stockUsed;
                    var incomingUsed = Math.Min(delta - stockUsed, incoming.GetValueOrDefault(code));
                    incoming[code] = incoming.GetValueOrDefault(code) - incomingUsed;
                }
                if (need <= 0) continue;
                run.Suggestions.Add(new PurchaseSuggestion
                {
                    RunId = run.Id,
                    MaterialCode = code,
                    MaterialName = materialNames.GetValueOrDefault(code),
                    Qty = decimal.Round(need, 4),
                    UnitCode = baseUnits.GetValueOrDefault(code),
                    Source = entry.Value.Source,
                    Status = PurchaseSuggestionStatuses.Suggested
                });
            }
        }

        // ---- Persist run + rows in one transaction --------------------
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var insertRun = CreateCommand(connection, transaction,
                "INSERT INTO mfg_planning_runs (id, company_id, run_date, status, notes, created_by) VALUES (@id, @company, @date, @status, @notes, @user)"))
            {
                Add(insertRun, "@id", FormatGuid(run.Id));
                Add(insertRun, "@company", company);
                Add(insertRun, "@date", run.RunDate);
                Add(insertRun, "@status", run.Status);
                Add(insertRun, "@notes", (object?)run.Notes ?? DBNull.Value);
                Add(insertRun, "@user", userId);
                await insertRun.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var planned in run.PlannedOrders)
            {
                if (planned.Id == Guid.Empty) planned.Id = Guid.NewGuid();
                planned.RunId = run.Id;
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO mfg_planned_orders (id, run_id, company_id, material_code, material_name, qty, unit_code, bom_id, due_date, source, status, created_by) VALUES (@id, @run, @company, @material, @materialName, @qty, @unit, @bom, @due, @source, @status, @user)");
                Add(insert, "@id", FormatGuid(planned.Id));
                Add(insert, "@run", FormatGuid(run.Id));
                Add(insert, "@company", company);
                Add(insert, "@material", planned.MaterialCode);
                Add(insert, "@materialName", (object?)planned.MaterialName ?? DBNull.Value);
                Add(insert, "@qty", planned.Qty);
                Add(insert, "@unit", planned.UnitCode);
                Add(insert, "@bom", planned.BomId is { } b ? FormatGuid(b) : DBNull.Value);
                Add(insert, "@due", (object?)planned.DueDate ?? DBNull.Value);
                Add(insert, "@source", (object?)planned.Source ?? DBNull.Value);
                Add(insert, "@status", planned.Status);
                Add(insert, "@user", userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var suggestion in run.Suggestions)
            {
                if (suggestion.Id == Guid.Empty) suggestion.Id = Guid.NewGuid();
                suggestion.RunId = run.Id;
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO mfg_purchase_suggestions (id, run_id, company_id, material_code, material_name, qty, unit_code, source, status, created_by) VALUES (@id, @run, @company, @material, @materialName, @qty, @unit, @source, @status, @user)");
                Add(insert, "@id", FormatGuid(suggestion.Id));
                Add(insert, "@run", FormatGuid(run.Id));
                Add(insert, "@company", company);
                Add(insert, "@material", suggestion.MaterialCode);
                Add(insert, "@materialName", (object?)suggestion.MaterialName ?? DBNull.Value);
                Add(insert, "@qty", suggestion.Qty);
                Add(insert, "@unit", (object?)suggestion.UnitCode ?? DBNull.Value);
                Add(insert, "@source", (object?)suggestion.Source ?? DBNull.Value);
                Add(insert, "@status", suggestion.Status);
                Add(insert, "@user", userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return run;
    }

    // ------------------------------------------------------------------
    // Queries
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<PlanningRun>> GetRunsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT id, run_date, status, notes FROM mfg_planning_runs WHERE company_id = @company ORDER BY created_at DESC");
        Add(command, "@company", companyId.Trim());
        var result = new List<PlanningRun>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new PlanningRun
                {
                    Id = ReadGuid(reader, 0),
                    RunDate = ReadString(reader, 1) ?? string.Empty,
                    Status = ReadString(reader, 2) ?? "Completed",
                    Notes = ReadString(reader, 3)
                });
            }
        }
        foreach (var run in result)
            await LoadRunRowsAsync(connection, companyId.Trim(), run, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<PlanningRun?> GetRunAsync(string companyId, Guid runId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        PlanningRun? run = null;
        await using (var command = CreateCommand(connection, null,
            "SELECT id, run_date, status, notes FROM mfg_planning_runs WHERE company_id = @company AND id = @id"))
        {
            Add(command, "@company", companyId.Trim());
            Add(command, "@id", FormatGuid(runId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                run = new PlanningRun
                {
                    Id = ReadGuid(reader, 0),
                    RunDate = ReadString(reader, 1) ?? string.Empty,
                    Status = ReadString(reader, 2) ?? "Completed",
                    Notes = ReadString(reader, 3)
                };
            }
        }
        if (run is not null)
            await LoadRunRowsAsync(connection, companyId.Trim(), run, cancellationToken).ConfigureAwait(false);
        return run;
    }

    private async Task LoadRunRowsAsync(DbConnection connection, string company, PlanningRun run, CancellationToken cancellationToken)
    {
        await using (var command = CreateCommand(connection, null,
            "SELECT id, material_code, material_name, qty, unit_code, bom_id, due_date, source, status, converted_order_id FROM mfg_planned_orders WHERE company_id = @company AND run_id = @run ORDER BY material_code"))
        {
            Add(command, "@company", company);
            Add(command, "@run", FormatGuid(run.Id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                run.PlannedOrders.Add(new PlannedOrder
                {
                    Id = ReadGuid(reader, 0),
                    RunId = run.Id,
                    MaterialCode = ReadString(reader, 1) ?? string.Empty,
                    MaterialName = ReadString(reader, 2),
                    Qty = ReadDecimal(reader, 3),
                    UnitCode = ReadString(reader, 4) ?? "PCS",
                    BomId = reader.IsDBNull(5) ? null : ReadGuid(reader, 5),
                    DueDate = ReadString(reader, 6),
                    Source = ReadString(reader, 7),
                    Status = ReadString(reader, 8) ?? PlannedOrderStatuses.Planned,
                    ConvertedOrderId = reader.IsDBNull(9) ? null : ReadGuid(reader, 9)
                });
            }
        }
        await using (var command = CreateCommand(connection, null,
            "SELECT id, material_code, material_name, qty, unit_code, source, status, converted_doc_id, converted_doc_no FROM mfg_purchase_suggestions WHERE company_id = @company AND run_id = @run ORDER BY material_code"))
        {
            Add(command, "@company", company);
            Add(command, "@run", FormatGuid(run.Id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                run.Suggestions.Add(new PurchaseSuggestion
                {
                    Id = ReadGuid(reader, 0),
                    RunId = run.Id,
                    MaterialCode = ReadString(reader, 1) ?? string.Empty,
                    MaterialName = ReadString(reader, 2),
                    Qty = ReadDecimal(reader, 3),
                    UnitCode = ReadString(reader, 4),
                    Source = ReadString(reader, 5),
                    Status = ReadString(reader, 6) ?? PurchaseSuggestionStatuses.Suggested,
                    ConvertedDocId = reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
                    ConvertedDocNo = ReadString(reader, 8)
                });
            }
        }
    }

    // ------------------------------------------------------------------
    // Feasibility (same explosion as planning)
    // ------------------------------------------------------------------
    public async Task<FeasibilityResult> CheckFeasibilityAsync(string companyId, string materialCode, decimal qty, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (qty <= 0) throw new ArgumentException("Quantity must be greater than zero.", nameof(qty));
        var company = companyId.Trim();
        var companyGuid = await ResolveCompanyGuidAsync(company, cancellationToken).ConfigureAwait(false);
        var bom = await _boms.GetDefaultForMaterialAsync(company, materialCode, cancellationToken).ConfigureAwait(false)
            ?? throw new AppError(ErrorCodes.BomNotFoundOrInactive,
                $"Material '{materialCode.Trim()}' has no active bill of materials.", materialCode.Trim());

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var materialId = await _boms.ResolveMaterialIdAsync(connection, null, company, materialCode, cancellationToken).ConfigureAwait(false);
        var outputEquivalent = await UnitEquivalents.ResolveAsync(connection, null, company, materialId, bom.OutputUnit, cancellationToken).ConfigureAwait(false);
        var outputQty = outputEquivalent == 0m ? qty : qty / outputEquivalent;
        var explosion = await _boms.ExplodeAsync(company, bom.Id, outputQty, cancellationToken).ConfigureAwait(false);
        var onHand = await LoadOnHandAsync(connection, company, cancellationToken).ConfigureAwait(false);
        var incoming = await LoadIncomingAsync(connection, companyGuid, cancellationToken).ConfigureAwait(false);
        var names = await LoadMaterialNamesAsync(connection, company, cancellationToken).ConfigureAwait(false);
        var rows = explosion.Requirements
            .GroupBy(r => r.MaterialCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new FeasibilityRow(
                g.Key, names.GetValueOrDefault(g.Key),
                decimal.Round(g.Sum(r => r.BaseQty), 4),
                onHand.GetValueOrDefault(g.Key),
                incoming.GetValueOrDefault(g.Key),
                g.First().UnitCode))
            .ToList();
        return new FeasibilityResult(materialCode.Trim(), qty, rows);
    }

    // ------------------------------------------------------------------
    // Conversions
    // ------------------------------------------------------------------
    public async Task<Guid> ConvertPlannedOrderAsync(string companyId, Guid plannedOrderId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var company = companyId.Trim();
        PlannedOrder? row = null;
        await using (var connection = CreateConnection())
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            row = await LoadPlannedAsync(connection, company, plannedOrderId, cancellationToken).ConfigureAwait(false);
        }
        if (row is null)
            throw new InvalidOperationException("Planned order was not found for this company.");
        if (row.Status != PlannedOrderStatuses.Planned)
            throw new AppError(ErrorCodes.PlanningConversionInvalid,
                $"Planned order for '{row.MaterialCode}' cannot be converted in status '{row.Status}'.",
                row.MaterialCode, row.Status);

        var bom = row.BomId is { } bomId
            ? await _boms.GetByIdAsync(company, bomId, cancellationToken).ConfigureAwait(false)
            : await _boms.GetDefaultForMaterialAsync(company, row.MaterialCode, cancellationToken).ConfigureAwait(false);
        if (bom is null)
            throw new AppError(ErrorCodes.BomNotFoundOrInactive,
                $"Material '{row.MaterialCode}' has no active bill of materials to convert with.", row.MaterialCode);

        await using var connection2 = CreateConnection();
        await connection2.OpenAsync(cancellationToken).ConfigureAwait(false);
        var materialId = await _boms.ResolveMaterialIdAsync(connection2, null, company, row.MaterialCode, cancellationToken).ConfigureAwait(false);
        var outputEquivalent = await UnitEquivalents.ResolveAsync(connection2, null, company, materialId, bom.OutputUnit, cancellationToken).ConfigureAwait(false);
        var outputQty = outputEquivalent == 0m ? row.Qty : row.Qty / outputEquivalent;
        var explosion = await _boms.ExplodeAsync(company, bom.Id, outputQty, cancellationToken).ConfigureAwait(false);
        var warehouseOptions = await _boms.GetWarehouseOptionsAsync(company, cancellationToken).ConfigureAwait(false);
        var firstWarehouse = warehouseOptions.Count > 0 ? warehouseOptions[0].Code : null;

        var order = new MfgOrder
        {
            DocDate = DateOnly.FromDateTime(DateTime.Today),
            ItemCode = row.MaterialCode,
            ItemName = row.MaterialName,
            Qty = row.Qty,
            UnitCode = row.UnitCode,
            WarehouseCode = firstWarehouse,
            BomId = bom.Id,
            BomVersion = bom.Version,
            PlannedEndDate = row.DueDate is { } due && DateOnly.TryParse(due, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            Notes = $"Planned order {row.Id:D} (planning run {row.RunId:D})"
        };
        foreach (var req in explosion.Requirements)
        {
            order.Lines.Add(new MfgOrderLine
            {
                OrderId = order.Id,
                LineKind = MfgLineKinds.Raw,
                ItemCode = req.MaterialCode,
                ItemName = req.MaterialName,
                Qty = req.Qty,
                UnitCode = req.UnitCode,
                UnitCost = req.StandardUnitCost,
                StandardUnitCost = req.StandardUnitCost,
                WarehouseCode = req.WarehouseCode
            });
        }
        await _orders.SaveDraftAsync(company, order, userId, cancellationToken).ConfigureAwait(false);

        await StampConvertedAsync(company, "mfg_planned_orders", plannedOrderId, PlannedOrderStatuses.Converted,
            "converted_order_id", FormatGuid(order.Id), cancellationToken).ConfigureAwait(false);
        return order.Id;
    }

    public async Task<(long DocId, string DocNo)> ConvertSuggestionAsync(string companyId, Guid suggestionId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var company = companyId.Trim();
        PurchaseSuggestion? row = null;
        await using (var connection = CreateConnection())
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            row = await LoadSuggestionAsync(connection, company, suggestionId, cancellationToken).ConfigureAwait(false);
        }
        if (row is null)
            throw new InvalidOperationException("Purchase suggestion was not found for this company.");
        if (row.Status != PurchaseSuggestionStatuses.Suggested)
            throw new AppError(ErrorCodes.PlanningConversionInvalid,
                $"Purchase suggestion for '{row.MaterialCode}' cannot be converted in status '{row.Status}'.",
                row.MaterialCode, row.Status);

        var companyGuid = await ResolveCompanyGuidAsync(company, cancellationToken).ConfigureAwait(false);
        long materialId;
        decimal unitPrice;
        await using (var connection = CreateConnection())
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            materialId = await _boms.ResolveMaterialIdAsync(connection, null, company, row.MaterialCode, cancellationToken).ConfigureAwait(false);
            unitPrice = await _boms.CurrentBaseRateAsync(connection, null, company, materialId, cancellationToken).ConfigureAwait(false);
        }
        var docNo = await _documents.NextDocNoAsync(CommercialDocSide.Purchase, companyGuid, CommercialDocKinds.PurchaseOrder, cancellationToken).ConfigureAwait(false);
        var draft = new CommercialDocumentDraft(
            companyGuid, CommercialDocKinds.PurchaseOrder, docNo,
            DateOnly.FromDateTime(DateTime.Today),
            PartyId: null, PartyName: null, WarehouseId: null,
            CurrencyCode: "SAR", ExchangeRate: 1m,
            Notes: $"Planning suggestion {row.Id:D} (planning run {row.RunId:D})",
            Lines: new[]
            {
                new CommercialLineDraft(materialId, row.MaterialCode,
                    row.MaterialName ?? row.MaterialCode, row.UnitCode, row.Qty, unitPrice)
            });
        var docId = await _documents.CreateDraftAsync(CommercialDocSide.Purchase, draft, userId, cancellationToken).ConfigureAwait(false);

        await using (var connection = CreateConnection())
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var stamp = CreateCommand(connection, transaction,
                    $"UPDATE mfg_purchase_suggestions SET status = @status, converted_doc_id = @doc, converted_doc_no = @docNo WHERE company_id = @company AND id = @id AND status = @from");
                Add(stamp, "@status", PurchaseSuggestionStatuses.Converted);
                Add(stamp, "@doc", docId);
                Add(stamp, "@docNo", docNo);
                Add(stamp, "@company", company);
                Add(stamp, "@id", FormatGuid(suggestionId));
                Add(stamp, "@from", PurchaseSuggestionStatuses.Suggested);
                if (await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new AppError(ErrorCodes.PlanningConversionInvalid,
                        $"Purchase suggestion for '{row.MaterialCode}' changed while converting.", row.MaterialCode, row.Status);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        return (docId, docNo);
    }

    public async Task CancelPlannedOrderAsync(string companyId, Guid plannedOrderId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await StampConvertedAsync(companyId.Trim(), "mfg_planned_orders", plannedOrderId, PlannedOrderStatuses.Cancelled,
            null, null, cancellationToken, fromStatus: PlannedOrderStatuses.Planned).ConfigureAwait(false);
    }

    public async Task CancelSuggestionAsync(string companyId, Guid suggestionId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await StampConvertedAsync(companyId.Trim(), "mfg_purchase_suggestions", suggestionId, PurchaseSuggestionStatuses.Cancelled,
            null, null, cancellationToken, fromStatus: PurchaseSuggestionStatuses.Suggested).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Loading helpers
    // ------------------------------------------------------------------
    private async Task<Dictionary<string, decimal>> LoadOnHandAsync(DbConnection connection, string company, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using var command = CreateCommand(connection, null,
            $"SELECT m.code, COALESCE(SUM(b.available_qty), 0) FROM materials m LEFT JOIN material_stock_balances b ON b.company_id = m.company_id AND b.material_id = m.material_id AND b.{NotDeleted} WHERE m.company_id = @company AND m.{NotDeleted} GROUP BY m.code");
        Add(command, "@company", company);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result[ReadString(reader, 0)!.Trim()] = ReadDecimal(reader, 1);
        return result;
    }

    private async Task<Dictionary<string, decimal>> LoadIncomingAsync(DbConnection connection, Guid companyGuid, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<(string Code, decimal Qty, string? Unit)>();
        await using (var command = CreateCommand(connection, null,
            "SELECT l.material_code, l.qty, l.unit FROM purchase_documents d JOIN purchase_document_lines l ON l.doc_id = d.doc_id WHERE d.company_id = @company AND d.doc_kind = 'PO' AND d.status = 'Posted' AND d.is_deleted = 0 AND l.material_code IS NOT NULL"))
        {
            Add(command, "@company", companyGuid.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add((ReadString(reader, 0)!, ReadDecimal(reader, 1), ReadString(reader, 2)));
        }
        // Materials key by company CODE (the documents key by Guid), so
        // resolve the code once and convert each line through it.
        var companyCode = await CompanyCodeForAsync(connection, companyGuid, cancellationToken).ConfigureAwait(false);
        if (companyCode is null) return result;
        foreach (var row in rows)
        {
            var materialId = await TryResolveMaterialIdAsync(connection, companyCode, row.Code, cancellationToken).ConfigureAwait(false);
            if (materialId is null) continue;
            var equivalent = await UnitEquivalents.ResolveAsync(connection, null, companyCode, materialId.Value, row.Unit, cancellationToken).ConfigureAwait(false);
            var code = row.Code.Trim();
            result[code] = result.GetValueOrDefault(code) + row.Qty * equivalent;
        }
        return result;
    }

    private async Task<Dictionary<string, string?>> LoadMaterialNamesAsync(DbConnection connection, string company, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await using var command = CreateCommand(connection, null,
            $"SELECT code, COALESCE(NULLIF(name, ''), NULLIF(name_en, ''), code) FROM materials WHERE company_id = @company AND {NotDeleted}");
        Add(command, "@company", company);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result[ReadString(reader, 0)!.Trim()] = ReadString(reader, 1);
        return result;
    }

    private async Task<Dictionary<string, string?>> LoadBaseUnitsAsync(DbConnection connection, string company, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await using var command = CreateCommand(connection, null,
            $"SELECT code, unit1 FROM materials WHERE company_id = @company AND {NotDeleted}");
        Add(command, "@company", company);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result[ReadString(reader, 0)!.Trim()] = ReadString(reader, 1);
        return result;
    }

    private async Task<PlannedOrder?> LoadPlannedAsync(DbConnection connection, string company, Guid id, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, null,
            "SELECT id, run_id, material_code, material_name, qty, unit_code, bom_id, due_date, source, status, converted_order_id FROM mfg_planned_orders WHERE company_id = @company AND id = @id");
        Add(command, "@company", company);
        Add(command, "@id", FormatGuid(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new PlannedOrder
        {
            Id = ReadGuid(reader, 0),
            RunId = ReadGuid(reader, 1),
            MaterialCode = ReadString(reader, 2) ?? string.Empty,
            MaterialName = ReadString(reader, 3),
            Qty = ReadDecimal(reader, 4),
            UnitCode = ReadString(reader, 5) ?? "PCS",
            BomId = reader.IsDBNull(6) ? null : ReadGuid(reader, 6),
            DueDate = ReadString(reader, 7),
            Source = ReadString(reader, 8),
            Status = ReadString(reader, 9) ?? PlannedOrderStatuses.Planned,
            ConvertedOrderId = reader.IsDBNull(10) ? null : ReadGuid(reader, 10)
        };
    }

    private async Task<PurchaseSuggestion?> LoadSuggestionAsync(DbConnection connection, string company, Guid id, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, null,
            "SELECT id, run_id, material_code, material_name, qty, unit_code, source, status, converted_doc_id, converted_doc_no FROM mfg_purchase_suggestions WHERE company_id = @company AND id = @id");
        Add(command, "@company", company);
        Add(command, "@id", FormatGuid(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new PurchaseSuggestion
        {
            Id = ReadGuid(reader, 0),
            RunId = ReadGuid(reader, 1),
            MaterialCode = ReadString(reader, 2) ?? string.Empty,
            MaterialName = ReadString(reader, 3),
            Qty = ReadDecimal(reader, 4),
            UnitCode = ReadString(reader, 5),
            Source = ReadString(reader, 6),
            Status = ReadString(reader, 7) ?? PurchaseSuggestionStatuses.Suggested,
            ConvertedDocId = reader.IsDBNull(8) ? null : Convert.ToInt64(reader.GetValue(8), CultureInfo.InvariantCulture),
            ConvertedDocNo = ReadString(reader, 9)
        };
    }

    private async Task StampConvertedAsync(string company, string table, Guid id, string newStatus,
        string? extraColumn, object? extraValue, CancellationToken cancellationToken, string? fromStatus = null)
    {
        var from = fromStatus ?? (table == "mfg_planned_orders" ? PlannedOrderStatuses.Planned : PurchaseSuggestionStatuses.Suggested);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var extra = extraColumn is null ? string.Empty : $", {extraColumn} = @extra";
            await using var stamp = CreateCommand(connection, transaction,
                $"UPDATE {table} SET status = @status{extra} WHERE company_id = @company AND id = @id AND status = @from");
            Add(stamp, "@status", newStatus);
            if (extraColumn is not null) Add(stamp, "@extra", extraValue!);
            Add(stamp, "@company", company);
            Add(stamp, "@id", FormatGuid(id));
            Add(stamp, "@from", from);
            if (await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new AppError(ErrorCodes.PlanningConversionInvalid,
                    $"Planning row '{id:D}' cannot change from '{from}' to '{newStatus}'.",
                    id.ToString("D"), from);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Guid> ResolveCompanyGuidAsync(string companyCode, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ResolveCompanyGuidAsync(connection, companyCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> ResolveCompanyGuidAsync(DbConnection connection, string companyCode, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, null,
            "SELECT company_id FROM companies WHERE company_code = @code");
        Add(command, "@code", companyCode.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull || !Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var guid) || guid == Guid.Empty)
            throw new InvalidOperationException($"Company '{companyCode.Trim()}' could not be resolved for planning.");
        return guid;
    }

    private async Task<string?> CompanyCodeForAsync(DbConnection connection, Guid companyGuid, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, null,
            "SELECT company_code FROM companies WHERE company_id = @id");
        Add(command, "@id", _provider == "PostgreSQL" ? (object)companyGuid : companyGuid.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private async Task<long?> TryResolveMaterialIdAsync(DbConnection connection, string companyKey, string code, CancellationToken cancellationToken, bool companyCodeKey = true)
    {
        await using var command = CreateCommand(connection, null,
            $"SELECT material_id FROM materials WHERE company_id = @company AND code = @code AND {NotDeleted} LIMIT 1");
        Add(command, "@company", companyKey.Trim());
        Add(command, "@code", code.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction != null) command.Transaction = transaction;
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

    private object FormatGuid(Guid value) => _provider == "PostgreSQL" ? value : value.ToString("D");

    private static Guid ReadGuid(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            _ => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!)
        };
    }

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal ReadDecimal(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
