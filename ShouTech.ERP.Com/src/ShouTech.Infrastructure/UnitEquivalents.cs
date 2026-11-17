using System.Data.Common;
using System.Globalization;

namespace ShouTech.Infrastructure;

/// <summary>
/// THE unit-equivalent resolution for the infrastructure layer (Fix Phase
/// Step 11). Stock is kept in the material's FIRST (base) unit; a quantity
/// expressed in another unit moves qty × equivalent(chosen unit), and a
/// unit price quoted per chosen unit is restated per base unit so
/// qty × cost keeps the line's value (owner definition, 2026-11-17 —
/// docs/MATCARD_FIXES.md defect 5).
///
/// This is the same rule the invoice path applies: the app layer's
/// <c>StkPost.ResolveUnitEquivalent</c> / <c>StkPost.ToBaseUnits</c> is the
/// synchronous app-layer original, and the delivery-note / order-chain
/// repositories previously each carried a private async copy of it. Those
/// copies now delegate here, and MfgOrderRepository consumes it directly —
/// there is exactly ONE async implementation of the rule in this layer.
///
/// Rules: the equivalence comes from the unit rows saved on the material
/// card (units_json carries the complete list; the unit1..unit5 column
/// pairs are the fallback for legacy rows). No unit chosen, the base unit
/// itself, an unknown or free-typed unit, or a stored equivalent of 0 or
/// less all resolve to 1 — the quantity is then already in base units.
/// </summary>
public static class UnitEquivalents
{
    /// <summary>
    /// Resolves the equivalence of a chosen unit relative to the material's
    /// first (base) unit. <paramref name="companyId"/> scopes the material
    /// lookup when the caller keys materials by company (MfgOrderRepository
    /// does); pass null when the connection is already scoped to one
    /// company's database and the lookup keys on material_id alone (the
    /// delivery-note / order-chain convention).
    /// </summary>
    public static async Task<decimal> ResolveAsync(
        DbConnection connection, DbTransaction transaction, string? companyId,
        long materialId, string? unitName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(unitName)) return 1m;
        var wanted = unitName.Trim();

        var units = new List<(string Name, decimal Factor)>();
        var sql = companyId is null
            ? """
              SELECT unit1, unit1_factor, unit2, unit2_factor, unit3, unit3_factor,
                     unit4, unit4_factor, unit5, unit5_factor, units_json
              FROM materials
              WHERE material_id = @material
              """
            : """
              SELECT unit1, unit1_factor, unit2, unit2_factor, unit3, unit3_factor,
                     unit4, unit4_factor, unit5, unit5_factor, units_json
              FROM materials
              WHERE company_id = @company AND material_id = @material
              """;
        await using (var command = CreateCommand(connection, transaction, sql))
        {
            if (companyId is not null) Add(command, "@company", companyId.Trim());
            Add(command, "@material", materialId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return 1m;

            var json = reader.IsDBNull(10) ? null : reader.GetValue(10)?.ToString();
            if (!string.IsNullOrWhiteSpace(json) && json.Trim() != "[]")
            {
                try
                {
                    var rows = System.Text.Json.JsonSerializer.Deserialize<List<UnitFactorRow>>(json)
                               ?? new List<UnitFactorRow>();
                    foreach (var row in rows)
                        if (!string.IsNullOrWhiteSpace(row.UnitName))
                            units.Add((row.UnitName.Trim(), row.Factor ?? 0m));
                }
                catch (System.Text.Json.JsonException)
                {
                    // Malformed legacy JSON: fall back to the column pairs.
                    units.Clear();
                }
            }

            if (units.Count == 0)
            {
                for (var i = 0; i < 5; i++)
                {
                    var name = reader.IsDBNull(i * 2) ? null : reader.GetValue(i * 2)?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var factor = reader.IsDBNull(i * 2 + 1)
                        ? 0m
                        : Convert.ToDecimal(reader.GetValue(i * 2 + 1), CultureInfo.InvariantCulture);
                    units.Add((name.Trim(), factor));
                }
            }
        }

        return ResolveFactor(units, wanted);
    }

    /// <summary>The pure core of the rule over an ordered unit list whose
    /// FIRST entry is the base unit (equivalent 1 by definition).</summary>
    public static decimal ResolveFactor(
        IReadOnlyList<(string Name, decimal Factor)> units, string? unitName)
    {
        if (units.Count == 0 || string.IsNullOrWhiteSpace(unitName)) return 1m;
        var wanted = unitName.Trim();

        // The first unit is the base unit: its equivalent is 1 by definition.
        if (string.Equals(wanted, units[0].Name, StringComparison.OrdinalIgnoreCase)) return 1m;

        foreach (var (name, factor) in units)
            if (string.Equals(wanted, name, StringComparison.OrdinalIgnoreCase))
                return factor > 0 ? factor : 1m;

        return 1m;
    }

    /// <summary>Restates a display-unit quantity and its per-display-unit
    /// price in base units: base_qty = qty × equivalent, and the price is
    /// divided by the same equivalent so qty × price is preserved.</summary>
    public static (decimal BaseQty, decimal BaseUnitCost) ToBase(
        decimal qty, decimal unitPrice, decimal equivalent)
    {
        var baseQty = qty * equivalent;
        var baseUnitCost = equivalent == 1m
            ? unitPrice
            : decimal.Round(unitPrice / equivalent, 6, MidpointRounding.AwayFromZero);
        return (baseQty, baseUnitCost);
    }

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

    /// <summary>One unit row as serialized into materials.units_json by the material card.</summary>
    private sealed class UnitFactorRow
    {
        public string? UnitName { get; set; }
        public decimal? Factor { get; set; }
    }
}
