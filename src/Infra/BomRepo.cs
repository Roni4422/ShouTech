using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral BOM master persistence + explosion (migration 058):
/// parameterized SQL only, company scoping on every statement,
/// single-transaction writes — the MfgOrderRepository conventions.
///
/// Explosion is multi-level and cycle-safe. A component that has an
/// active default BOM of its own is expanded recursively; the recursion
/// carries the chain of finished materials it is currently expanding,
/// and meeting one of them again is a cycle, refused with MFG-1001.
/// Quantities: each line contributes qty x (output factor) x
/// (1 + scrap%/100) in the line's own unit; the stock-facing BaseQty is
/// that quantity converted through UnitEquivalents (the ONE conversion
/// rule, Step 11). When a component is expanded, its requirement is
/// converted line-unit → base → the sub-BOM's output unit, so nested
/// batch sizes stay exact.
/// </summary>
public sealed class BomRepository : IBomRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public BomRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    internal string Provider => _provider;
    internal string ConnectionString => _connectionString;

    private string NotDeleted => _provider == "PostgreSQL" ? "is_deleted = false" : "is_deleted = 0";
    private string BoolTrue => _provider == "PostgreSQL" ? "true" : "1";
    private string NowExpr => _provider == "PostgreSQL" ? "now()" : "datetime('now')";

    // ------------------------------------------------------------------
    // Queries
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<Bom>> GetAllAsync(string companyId, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var activeFilter = includeInactive ? string.Empty : $" AND is_active = {BoolTrue}";
        await using var command = CreateCommand(connection, null,
            $"SELECT id, material_code, material_name, version, is_active, is_default, output_qty, output_unit, notes, input_document_kind, output_document_kind, input_warehouse_code, output_warehouse_code FROM mfg_boms WHERE company_id = @company AND {NotDeleted}{activeFilter} ORDER BY material_code, version");
        Add(command, "@company", companyId.Trim());
        var result = new List<Bom>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                result.Add(ReadBom(reader));
        }
        foreach (var bom in result)
        {
            bom.Lines = await ReadLinesAsync(connection, bom.Id, cancellationToken).ConfigureAwait(false);
            bom.Expenses = await ReadExpensesAsync(connection, bom.Id, cancellationToken).ConfigureAwait(false);
            bom.Outputs = await ReadOutputsAsync(connection, bom.Id, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    public async Task<Bom?> GetByIdAsync(string companyId, Guid bomId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await LoadBomAsync(connection, null, companyId, bomId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Bom?> GetDefaultForMaterialAsync(string companyId, string materialCode, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await LoadDefaultBomAsync(connection, null, companyId, materialCode, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Save / delete
    // ------------------------------------------------------------------
    public async Task SaveAsync(string companyId, Bom bom, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(bom);
        if (string.IsNullOrWhiteSpace(bom.MaterialCode))
            throw new InvalidOperationException("The BOM finished material code is required.");
        if (bom.OutputQty <= 0)
            throw new InvalidOperationException("The BOM output quantity must be greater than zero.");
        if (bom.Lines.Count == 0)
            throw new InvalidOperationException("A bill of materials requires at least one component line.");
        if (bom.Outputs.Count == 0)
            bom.Outputs.Add(new BomOutput { BomId = bom.Id, MaterialCode = bom.MaterialCode, MaterialName = bom.MaterialName, OutputKind = "MAIN", Qty = bom.OutputQty, UnitCode = bom.OutputUnit });
        if (bom.Outputs.Count(o => string.Equals(o.OutputKind, "MAIN", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidOperationException("A manufacturing template must contain exactly one MAIN output.");
        if (bom.Outputs.Any(o => o.Qty <= 0 || string.IsNullOrWhiteSpace(o.MaterialCode)))
            throw new InvalidOperationException("Every output requires a material and a quantity greater than zero.");
        if (bom.Outputs.Any(o => !new[] { "MAIN", "CO_PRODUCT", "BY_PRODUCT", "SCRAP" }.Contains(o.OutputKind?.Trim().ToUpperInvariant())))
            throw new InvalidOperationException("Output type must be MAIN, CO_PRODUCT, BY_PRODUCT or SCRAP.");
        if (bom.Outputs.Any(o => o.CostSharePct is < 0 or > 100) || bom.Outputs.Sum(o => o.CostSharePct) > 100m)
            throw new InvalidOperationException("Output cost-share percentages must be between 0 and 100 and cannot exceed 100 in total.");
        if (!new[] { "MATERIAL_ISSUE", "STOCK_OUT" }.Contains(bom.InputDocumentKind.Trim().ToUpperInvariant()) ||
            !new[] { "MATERIAL_RECEIPT", "STOCK_IN" }.Contains(bom.OutputDocumentKind.Trim().ToUpperInvariant()))
            throw new InvalidOperationException("Manufacturing input and output document types are invalid.");
        if (bom.Expenses.Any(e => e.Amount < 0 || string.IsNullOrWhiteSpace(e.ExpenseCode) ||
            string.IsNullOrWhiteSpace(e.DebitAccountCode) || string.IsNullOrWhiteSpace(e.CreditAccountCode)))
            throw new InvalidOperationException("Each production expense requires a code, a non-negative amount, and debit and credit accounts.");
        foreach (var line in bom.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.ComponentCode))
                throw new InvalidOperationException("Every BOM line requires a component code.");
            if (string.Equals(line.ComponentCode.Trim(), bom.MaterialCode.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new AppError(ErrorCodes.BomCycleDetected,
                    $"Bill of materials for '{bom.MaterialCode.Trim()}' references itself directly.", bom.MaterialCode.Trim());
            if (line.Qty <= 0)
                throw new InvalidOperationException($"Line quantity for '{line.ComponentCode}' must be greater than zero.");
            if (line.ScrapPct is < 0 or >= 100)
                throw new InvalidOperationException($"Scrap percentage for '{line.ComponentCode}' must be between 0 and 100.");
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Every referenced material must exist in this company.
            _ = await ResolveMaterialIdAsync(connection, transaction, companyId, bom.MaterialCode, cancellationToken).ConfigureAwait(false);
            foreach (var line in bom.Lines)
                _ = await ResolveMaterialIdAsync(connection, transaction, companyId, line.ComponentCode, cancellationToken).ConfigureAwait(false);
            foreach (var output in bom.Outputs)
                _ = await ResolveMaterialIdAsync(connection, transaction, companyId, output.MaterialCode, cancellationToken).ConfigureAwait(false);

            // Transitive cycle check against the CURRENT default-BOM
            // graph with this BOM applied: expanding the finished
            // material must never come back to it.
            await AssertNoCycleAsync(connection, transaction, companyId, bom, cancellationToken).ConfigureAwait(false);

            // Standard cost snapshot: the valuation engine's current
            // rate per component (balance value / qty, else the card's
            // avg / standard / last purchase — the engine fallback chain).
            foreach (var line in bom.Lines)
            {
                var materialId = await ResolveMaterialIdAsync(connection, transaction, companyId, line.ComponentCode, cancellationToken).ConfigureAwait(false);
                var equivalent = await UnitEquivalents.ResolveAsync(connection, transaction, companyId, materialId, line.UnitCode, cancellationToken).ConfigureAwait(false);
                var baseRate = await CurrentBaseRateAsync(connection, transaction, companyId, materialId, cancellationToken).ConfigureAwait(false);
                // Snapshot is per LINE unit: rate per base x equivalent.
                line.StandardUnitCost = decimal.Round(baseRate * equivalent, 4, MidpointRounding.AwayFromZero);
            }

            if (bom.Id == Guid.Empty) bom.Id = Guid.NewGuid();
            var exists = await ScalarExistsAsync(connection, transaction,
                $"SELECT COUNT(*) FROM mfg_boms WHERE company_id = @company AND id = @id AND {NotDeleted}",
                ("@company", companyId.Trim()), ("@id", FormatGuid(bom.Id))).ConfigureAwait(false);

            if (bom.IsDefault)
            {
                await using var clear = CreateCommand(connection, transaction,
                    $"UPDATE mfg_boms SET is_default = {(_provider == "PostgreSQL" ? "false" : "0")}, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND material_code = @material AND id <> @id AND {NotDeleted}");
                Add(clear, "@user", userId);
                Add(clear, "@company", companyId.Trim());
                Add(clear, "@material", bom.MaterialCode.Trim());
                Add(clear, "@id", FormatGuid(bom.Id));
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO mfg_boms (id, company_id, material_code, material_name, version, is_active, is_default, output_qty, output_unit, notes, input_document_kind, output_document_kind, input_warehouse_code, output_warehouse_code, created_by) VALUES (@id, @company, @material, @materialName, @version, @active, @isDefault, @outputQty, @outputUnit, @notes, @inputKind, @outputKind, @inputWarehouse, @outputWarehouse, @user)");
                BindBom(insert, companyId, bom, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE mfg_boms SET material_code = @material, material_name = @materialName, version = @version, is_active = @active, is_default = @isDefault, output_qty = @outputQty, output_unit = @outputUnit, notes = @notes, input_document_kind = @inputKind, output_document_kind = @outputKind, input_warehouse_code = @inputWarehouse, output_warehouse_code = @outputWarehouse, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
                BindBom(update, companyId, bom, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Bill of materials could not be updated (missing or deleted).");
                foreach (var table in new[] { "mfg_bom_lines", "mfg_bom_expenses", "mfg_bom_outputs" })
                {
                    await using var deleteChild = CreateCommand(connection, transaction, $"DELETE FROM {table} WHERE bom_id = @id");
                    Add(deleteChild, "@id", FormatGuid(bom.Id));
                    await deleteChild.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            var lineNo = 0;
            foreach (var line in bom.Lines)
            {
                line.BomId = bom.Id;
                line.LineNo = ++lineNo;
                if (line.Id == Guid.Empty) line.Id = Guid.NewGuid();
                await using var insertLine = CreateCommand(connection, transaction,
                    "INSERT INTO mfg_bom_lines (id, bom_id, line_no, component_code, component_name, qty, unit_code, scrap_pct, warehouse_code, standard_unit_cost) VALUES (@id, @bom, @lineNo, @component, @componentName, @qty, @unit, @scrap, @warehouse, @std)");
                Add(insertLine, "@id", FormatGuid(line.Id));
                Add(insertLine, "@bom", FormatGuid(bom.Id));
                Add(insertLine, "@lineNo", line.LineNo);
                Add(insertLine, "@component", line.ComponentCode.Trim());
                Add(insertLine, "@componentName", (object?)NullIfBlank(line.ComponentName) ?? DBNull.Value);
                Add(insertLine, "@qty", line.Qty);
                Add(insertLine, "@unit", (object?)NullIfBlank(line.UnitCode) ?? DBNull.Value);
                Add(insertLine, "@scrap", line.ScrapPct);
                Add(insertLine, "@warehouse", (object?)NullIfBlank(line.WarehouseCode) ?? DBNull.Value);
                Add(insertLine, "@std", line.StandardUnitCost);
                await insertLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var expenseNo = 0;
            foreach (var expense in bom.Expenses)
            {
                expense.BomId = bom.Id;
                expense.LineNo = ++expenseNo;
                if (expense.Id == Guid.Empty) expense.Id = Guid.NewGuid();
                await using var insertExpense = CreateCommand(connection, transaction,
                    "INSERT INTO mfg_bom_expenses (id, bom_id, line_no, expense_code, description, amount, debit_account_code, credit_account_code, cost_center) VALUES (@id, @bom, @lineNo, @code, @description, @amount, @debit, @credit, @costCenter)");
                Add(insertExpense, "@id", FormatGuid(expense.Id));
                Add(insertExpense, "@bom", FormatGuid(bom.Id));
                Add(insertExpense, "@lineNo", expense.LineNo);
                Add(insertExpense, "@code", expense.ExpenseCode.Trim());
                Add(insertExpense, "@description", (object?)NullIfBlank(expense.Description) ?? DBNull.Value);
                Add(insertExpense, "@amount", expense.Amount);
                Add(insertExpense, "@debit", (object?)NullIfBlank(expense.DebitAccountCode) ?? DBNull.Value);
                Add(insertExpense, "@credit", (object?)NullIfBlank(expense.CreditAccountCode) ?? DBNull.Value);
                Add(insertExpense, "@costCenter", (object?)NullIfBlank(expense.CostCenter) ?? DBNull.Value);
                await insertExpense.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var outputNo = 0;
            foreach (var output in bom.Outputs)
            {
                output.BomId = bom.Id;
                output.LineNo = ++outputNo;
                if (output.Id == Guid.Empty) output.Id = Guid.NewGuid();
                await using var insertOutput = CreateCommand(connection, transaction,
                    "INSERT INTO mfg_bom_outputs (id, bom_id, line_no, material_code, material_name, output_kind, qty, unit_code, warehouse_code, cost_share_pct) VALUES (@id, @bom, @lineNo, @material, @materialName, @kind, @qty, @unit, @warehouse, @share)");
                Add(insertOutput, "@id", FormatGuid(output.Id));
                Add(insertOutput, "@bom", FormatGuid(bom.Id));
                Add(insertOutput, "@lineNo", output.LineNo);
                Add(insertOutput, "@material", output.MaterialCode.Trim());
                Add(insertOutput, "@materialName", (object?)NullIfBlank(output.MaterialName) ?? DBNull.Value);
                Add(insertOutput, "@kind", string.IsNullOrWhiteSpace(output.OutputKind) ? "MAIN" : output.OutputKind.Trim().ToUpperInvariant());
                Add(insertOutput, "@qty", output.Qty);
                Add(insertOutput, "@unit", (object?)NullIfBlank(output.UnitCode) ?? DBNull.Value);
                Add(insertOutput, "@warehouse", (object?)NullIfBlank(output.WarehouseCode) ?? DBNull.Value);
                Add(insertOutput, "@share", output.CostSharePct);
                await insertOutput.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SoftDeleteAsync(string companyId, Guid bomId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"UPDATE mfg_boms SET is_deleted = {BoolTrue}, is_active = {(_provider == "PostgreSQL" ? "false" : "0")}, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
        Add(command, "@user", userId);
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(bomId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Bill of materials was not found for this company.");
    }

    // ------------------------------------------------------------------
    // Explosion
    // ------------------------------------------------------------------
    public async Task<BomExplosion> ExplodeAsync(string companyId, Guid bomId, decimal outputQty, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (outputQty <= 0) throw new ArgumentException("Output quantity must be greater than zero.", nameof(outputQty));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var bom = await LoadBomAsync(connection, null, companyId, bomId, cancellationToken).ConfigureAwait(false)
            ?? throw new AppError(ErrorCodes.BomNotFoundOrInactive,
                $"Bill of materials '{bomId:D}' was not found for this company.", bomId.ToString("D"));
        if (!bom.IsActive)
            throw new AppError(ErrorCodes.BomNotFoundOrInactive,
                $"Bill of materials for '{bom.MaterialCode}' version {bom.Version} is not active.", bom.MaterialCode);
        var acc = new Dictionary<(string Code, string? Warehouse, decimal Std, string? Unit), (string? Name, decimal Qty, decimal BaseQty, int Level)>();
        var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bom.MaterialCode.Trim() };
        await ExplodeIntoAsync(connection, companyId, bom, outputQty, level: 0, path, acc, cancellationToken).ConfigureAwait(false);
        var requirements = acc
            .Select(kv => new BomRequirement(kv.Key.Code, kv.Value.Name, decimal.Round(kv.Value.Qty, 4), kv.Key.Unit,
                decimal.Round(kv.Value.BaseQty, 4), kv.Key.Std, kv.Key.Warehouse, kv.Value.Level))
            .OrderBy(r => r.Level).ThenBy(r => r.MaterialCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new BomExplosion(bom.Id, bom.MaterialCode, bom.Version, outputQty, requirements);
    }

    public async Task<BomExplosion> ExplodeForMaterialAsync(string companyId, string materialCode, decimal outputQty, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var bom = await GetDefaultForMaterialAsync(companyId, materialCode, cancellationToken).ConfigureAwait(false)
            ?? throw new AppError(ErrorCodes.BomNotFoundOrInactive,
                $"Material '{materialCode.Trim()}' has no active bill of materials.", materialCode.Trim());
        return await ExplodeAsync(companyId, bom.Id, outputQty, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExplodeIntoAsync(
        DbConnection connection, string companyId, Bom bom, decimal outputQtyInBomUnit, int level,
        HashSet<string> path,
        Dictionary<(string Code, string? Warehouse, decimal Std, string? Unit), (string? Name, decimal Qty, decimal BaseQty, int Level)> acc,
        CancellationToken cancellationToken)
    {
        var factor = outputQtyInBomUnit / bom.OutputQty;
        foreach (var line in bom.Lines)
        {
            var requiredDisplay = line.Qty * factor * (1m + line.ScrapPct / 100m);
            var componentCode = line.ComponentCode.Trim();
            var subBom = await LoadDefaultBomAsync(connection, null, companyId, componentCode, cancellationToken).ConfigureAwait(false);
            if (subBom is not null)
            {
                if (path.Contains(componentCode))
                    throw new AppError(ErrorCodes.BomCycleDetected,
                        $"Bill of materials for '{bom.MaterialCode}' references '{componentCode}', which (transitively) produces a material already on the explosion path.",
                        bom.MaterialCode);
                var materialId = await ResolveMaterialIdAsync(connection, null, companyId, componentCode, cancellationToken).ConfigureAwait(false);
                var lineEquivalent = await UnitEquivalents.ResolveAsync(connection, null, companyId, materialId, line.UnitCode, cancellationToken).ConfigureAwait(false);
                var baseQty = requiredDisplay * lineEquivalent;
                var outputEquivalent = await UnitEquivalents.ResolveAsync(connection, null, companyId, materialId, subBom.OutputUnit, cancellationToken).ConfigureAwait(false);
                var subOutputQty = outputEquivalent == 0m ? baseQty : baseQty / outputEquivalent;
                path.Add(componentCode);
                await ExplodeIntoAsync(connection, companyId, subBom, subOutputQty, level + 1, path, acc, cancellationToken).ConfigureAwait(false);
                path.Remove(componentCode);
            }
            else
            {
                var materialId = await ResolveMaterialIdAsync(connection, null, companyId, componentCode, cancellationToken).ConfigureAwait(false);
                var equivalent = await UnitEquivalents.ResolveAsync(connection, null, companyId, materialId, line.UnitCode, cancellationToken).ConfigureAwait(false);
                var baseQty = requiredDisplay * equivalent;
                var key = (componentCode, NullIfBlank(line.WarehouseCode), line.StandardUnitCost, NullIfBlank(line.UnitCode));
                if (acc.TryGetValue(key, out var existing))
                    acc[key] = (existing.Name, existing.Qty + requiredDisplay, existing.BaseQty + baseQty, Math.Min(existing.Level, level));
                else
                    acc[key] = (line.ComponentName, requiredDisplay, baseQty, level);
            }
        }
    }

    /// <summary>Save-time transitive cycle guard: with the candidate BOM
    /// applied as its material's default, expanding the material must
    /// never return to it. Uses the same default-BOM graph explosion
    /// walks; other materials' saved BOMs are read as-is.</summary>
    private async Task AssertNoCycleAsync(DbConnection connection, DbTransaction transaction, string companyId, Bom candidate, CancellationToken cancellationToken)
    {
        var target = candidate.MaterialCode.Trim();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        foreach (var line in candidate.Lines) stack.Push(line.ComponentCode.Trim());
        while (stack.Count > 0)
        {
            var code = stack.Pop();
            if (string.Equals(code, target, StringComparison.OrdinalIgnoreCase))
                throw new AppError(ErrorCodes.BomCycleDetected,
                    $"Bill of materials for '{target}' references itself through '{code}'.", target);
            if (!visited.Add(code)) continue;
            var sub = await LoadDefaultBomAsync(connection, transaction, companyId, code, cancellationToken).ConfigureAwait(false);
            if (sub is null) continue;
            // If the sub-BOM IS the candidate being saved (same material
            // re-saved), its lines are the candidate's lines.
            var lines = sub.MaterialCode.Equals(target, StringComparison.OrdinalIgnoreCase) ? candidate.Lines : sub.Lines;
            foreach (var line in lines) stack.Push(line.ComponentCode.Trim());
        }
    }

    // ------------------------------------------------------------------
    // Pickers
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<MfgMaterialOption>> GetMaterialOptionsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT m.material_id, m.code, COALESCE(NULLIF(m.name, ''), m.code), m.unit1, m.avg_cost, m.standard_cost, m.last_purchase_cost FROM materials m WHERE m.company_id = @company AND m.{NotDeleted} ORDER BY m.code");
        Add(command, "@company", companyId.Trim());
        var result = new List<MfgMaterialOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var materialId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            var avg = ReadDecimal(reader, 4);
            var std = ReadDecimal(reader, 5);
            var last = ReadDecimal(reader, 6);
            result.Add(new MfgMaterialOption(
                ReadString(reader, 1) ?? string.Empty, ReadString(reader, 2), ReadString(reader, 3),
                avg > 0 ? avg : std > 0 ? std : last, materialId));
        }
        return result;
    }

    public async Task<IReadOnlyList<MfgWarehouseOption>> GetWarehouseOptionsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT warehouse_id, CAST(warehouse_code AS TEXT), COALESCE(NULLIF(warehouse_name_en, ''), warehouse_name_ar) FROM warehouses WHERE company_id = @company AND {NotDeleted} ORDER BY warehouse_id");
        Add(command, "@company", companyId.Trim());
        var result = new List<MfgWarehouseOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new MfgWarehouseOption(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                ReadString(reader, 1) ?? string.Empty, ReadString(reader, 2)));
        return result;
    }

    // ------------------------------------------------------------------
    // Loading helpers
    // ------------------------------------------------------------------
    internal async Task<Bom?> LoadBomAsync(DbConnection connection, DbTransaction? transaction, string companyId, Guid bomId, CancellationToken cancellationToken)
    {
        Bom? bom = null;
        await using (var command = CreateCommand(connection, transaction,
            $"SELECT id, material_code, material_name, version, is_active, is_default, output_qty, output_unit, notes, input_document_kind, output_document_kind, input_warehouse_code, output_warehouse_code FROM mfg_boms WHERE company_id = @company AND id = @id AND {NotDeleted}"))
        {
            Add(command, "@company", companyId.Trim());
            Add(command, "@id", FormatGuid(bomId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                bom = ReadBom(reader);
        }
        if (bom is not null)
        {
            bom.Lines = await ReadLinesAsync(connection, bom.Id, cancellationToken, transaction).ConfigureAwait(false);
            bom.Expenses = await ReadExpensesAsync(connection, bom.Id, cancellationToken, transaction).ConfigureAwait(false);
            bom.Outputs = await ReadOutputsAsync(connection, bom.Id, cancellationToken, transaction).ConfigureAwait(false);
        }
        return bom;
    }

    internal async Task<Bom?> LoadDefaultBomAsync(DbConnection connection, DbTransaction? transaction, string companyId, string materialCode, CancellationToken cancellationToken)
    {
        Guid? id = null;
        await using (var command = CreateCommand(connection, transaction,
            $"SELECT id FROM mfg_boms WHERE company_id = @company AND material_code = @material AND is_active = {BoolTrue} AND {NotDeleted} ORDER BY is_default DESC, version DESC LIMIT 1"))
        {
            Add(command, "@company", companyId.Trim());
            Add(command, "@material", materialCode.Trim());
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
                id = ReadGuidValue(value);
        }
        return id is null ? null : await LoadBomAsync(connection, transaction, companyId, id.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<BomLine>> ReadLinesAsync(DbConnection connection, Guid bomId, CancellationToken cancellationToken, DbTransaction? transaction = null)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT id, bom_id, line_no, component_code, component_name, qty, unit_code, scrap_pct, warehouse_code, standard_unit_cost FROM mfg_bom_lines WHERE bom_id = @bom ORDER BY line_no");
        Add(command, "@bom", FormatGuid(bomId));
        var result = new List<BomLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new BomLine
            {
                Id = ReadGuid(reader, 0),
                BomId = ReadGuid(reader, 1),
                LineNo = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                ComponentCode = ReadString(reader, 3) ?? string.Empty,
                ComponentName = ReadString(reader, 4),
                Qty = ReadDecimal(reader, 5),
                UnitCode = ReadString(reader, 6),
                ScrapPct = ReadDecimal(reader, 7),
                WarehouseCode = ReadString(reader, 8),
                StandardUnitCost = ReadDecimal(reader, 9)
            });
        }
        return result;
    }

    private async Task<List<BomExpense>> ReadExpensesAsync(DbConnection connection, Guid bomId, CancellationToken cancellationToken, DbTransaction? transaction = null)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT id, bom_id, line_no, expense_code, description, amount, debit_account_code, credit_account_code, cost_center FROM mfg_bom_expenses WHERE bom_id = @bom ORDER BY line_no");
        Add(command, "@bom", FormatGuid(bomId));
        var result = new List<BomExpense>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new BomExpense
            {
                Id = ReadGuid(reader, 0), BomId = ReadGuid(reader, 1),
                LineNo = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                ExpenseCode = ReadString(reader, 3) ?? string.Empty, Description = ReadString(reader, 4),
                Amount = ReadDecimal(reader, 5), DebitAccountCode = ReadString(reader, 6),
                CreditAccountCode = ReadString(reader, 7), CostCenter = ReadString(reader, 8)
            });
        return result;
    }

    private async Task<List<BomOutput>> ReadOutputsAsync(DbConnection connection, Guid bomId, CancellationToken cancellationToken, DbTransaction? transaction = null)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT id, bom_id, line_no, material_code, material_name, output_kind, qty, unit_code, warehouse_code, cost_share_pct FROM mfg_bom_outputs WHERE bom_id = @bom ORDER BY line_no");
        Add(command, "@bom", FormatGuid(bomId));
        var result = new List<BomOutput>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new BomOutput
            {
                Id = ReadGuid(reader, 0), BomId = ReadGuid(reader, 1),
                LineNo = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                MaterialCode = ReadString(reader, 3) ?? string.Empty, MaterialName = ReadString(reader, 4),
                OutputKind = ReadString(reader, 5) ?? "MAIN", Qty = ReadDecimal(reader, 6),
                UnitCode = ReadString(reader, 7), WarehouseCode = ReadString(reader, 8),
                CostSharePct = ReadDecimal(reader, 9)
            });
        return result;
    }

    /// <summary>The valuation engine's current rate per BASE unit for a
    /// material: total balance value / total qty across warehouses, else
    /// the card fallback chain (avg, last purchase, standard).</summary>
    internal async Task<decimal> CurrentBaseRateAsync(DbConnection connection, DbTransaction? transaction, string companyId, long materialId, CancellationToken cancellationToken)
    {
        await using var balances = CreateCommand(connection, transaction,
            $"SELECT COALESCE(SUM(available_qty), 0), COALESCE(SUM(stock_value), 0) FROM material_stock_balances WHERE company_id = @company AND material_id = @material AND {NotDeleted}");
        Add(balances, "@company", companyId.Trim());
        Add(balances, "@material", materialId);
        await using (var reader = await balances.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var qty = ReadDecimal(reader, 0);
                var value = ReadDecimal(reader, 1);
                if (qty > 0 && value > 0) return decimal.Round(value / qty, 4, MidpointRounding.AwayFromZero);
            }
        }
        await using var card = CreateCommand(connection, transaction,
            $"SELECT avg_cost, last_purchase_cost, standard_cost FROM materials WHERE company_id = @company AND material_id = @material AND {NotDeleted} LIMIT 1");
        Add(card, "@company", companyId.Trim());
        Add(card, "@material", materialId);
        await using var cardReader = await card.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await cardReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var avg = ReadDecimal(cardReader, 0);
            if (avg > 0) return avg;
            var last = ReadDecimal(cardReader, 1);
            if (last > 0) return last;
            return ReadDecimal(cardReader, 2);
        }
        return 0m;
    }

    internal async Task<long> ResolveMaterialIdAsync(DbConnection connection, DbTransaction? transaction, string companyId, string itemCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            throw new InvalidOperationException("A material code is required.");
        await using var command = CreateCommand(connection, transaction,
            $"SELECT material_id FROM materials WHERE company_id = @company AND code = @code AND {NotDeleted} LIMIT 1");
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", itemCode.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
            throw new InvalidOperationException($"Material '{itemCode.Trim()}' does not exist in the current company.");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<bool> ScalarExistsAsync(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var (name, value) in parameters) Add(command, name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    private void BindBom(DbCommand command, string companyId, Bom bom, string userId)
    {
        Add(command, "@id", FormatGuid(bom.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@material", bom.MaterialCode.Trim());
        Add(command, "@materialName", (object?)NullIfBlank(bom.MaterialName) ?? DBNull.Value);
        Add(command, "@version", bom.Version);
        Add(command, "@active", _provider == "PostgreSQL" ? (object)bom.IsActive : (bom.IsActive ? 1 : 0));
        Add(command, "@isDefault", _provider == "PostgreSQL" ? (object)bom.IsDefault : (bom.IsDefault ? 1 : 0));
        Add(command, "@outputQty", bom.OutputQty);
        Add(command, "@outputUnit", string.IsNullOrWhiteSpace(bom.OutputUnit) ? "PCS" : bom.OutputUnit.Trim());
        Add(command, "@notes", (object?)NullIfBlank(bom.Notes) ?? DBNull.Value);
        Add(command, "@inputKind", string.IsNullOrWhiteSpace(bom.InputDocumentKind) ? "MATERIAL_ISSUE" : bom.InputDocumentKind.Trim());
        Add(command, "@outputKind", string.IsNullOrWhiteSpace(bom.OutputDocumentKind) ? "MATERIAL_RECEIPT" : bom.OutputDocumentKind.Trim());
        Add(command, "@inputWarehouse", (object?)NullIfBlank(bom.InputWarehouseCode) ?? DBNull.Value);
        Add(command, "@outputWarehouse", (object?)NullIfBlank(bom.OutputWarehouseCode) ?? DBNull.Value);
        Add(command, "@user", userId);
    }

    private Bom ReadBom(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        MaterialCode = ReadString(reader, 1) ?? string.Empty,
        MaterialName = ReadString(reader, 2),
        Version = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
        IsActive = !reader.IsDBNull(4) && Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture),
        IsDefault = !reader.IsDBNull(5) && Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture),
        OutputQty = ReadDecimal(reader, 6),
        OutputUnit = ReadString(reader, 7) ?? "PCS",
        Notes = ReadString(reader, 8),
        InputDocumentKind = ReadString(reader, 9) ?? "MATERIAL_ISSUE",
        OutputDocumentKind = ReadString(reader, 10) ?? "MATERIAL_RECEIPT",
        InputWarehouseCode = ReadString(reader, 11),
        OutputWarehouseCode = ReadString(reader, 12)
    };

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    private static Guid ReadGuidValue(object value) => value switch
    {
        Guid g => g,
        string s when Guid.TryParse(s, out var parsed) => parsed,
        _ => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!)
    };

    private static Guid ReadGuid(DbDataReader reader, int ordinal) => ReadGuidValue(reader.GetValue(ordinal));

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
