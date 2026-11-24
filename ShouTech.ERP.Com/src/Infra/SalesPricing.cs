using System.Data.Common;
using System.Globalization;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.Infrastructure;

/// <summary>
/// Contract-price hook (W7a step a). Since W7c the contracts module
/// supplies <see cref="ContractPriceSource"/> (ContractRepository.cs),
/// which reads the agreed contract lines;
/// <see cref="NullContractPriceSource"/> remains for callers that
/// explicitly opt out. The resolver's order does not change.
/// </summary>
public interface IContractPriceSource
{
    decimal? TryGetPrice(
        DbConnection connection, DbTransaction transaction, string provider,
        IReadOnlyList<string> companyKeys, string? partyCode,
        long materialId, string materialCode, string? unit, string? currency, string asOfDate);
}

/// <summary>The pre-W7c contract source: no contracts exist, so no hit.</summary>
public sealed class NullContractPriceSource : IContractPriceSource
{
    public static readonly NullContractPriceSource Instance = new();

    public decimal? TryGetPrice(
        DbConnection connection, DbTransaction transaction, string provider,
        IReadOnlyList<string> companyKeys, string? partyCode,
        long materialId, string materialCode, string? unit, string? currency, string asOfDate)
        => null;
}

/// <summary>
/// THE sales price-resolution path (sales cycle W7a, spec §12). Before
/// this class every window took the material-card price or manual entry;
/// the stored pricing data (customer_prices, material_price_list, party
/// price_tier, discount_rules, bonus fields) was written but never read.
///
/// Resolution order — first hit wins, every level company-scoped and,
/// where the data supports it, filtered by validity dates, UOM and
/// currency:
///   (a) contract prices via <see cref="IContractPriceSource"/> (hook;
///       returns nothing until the W7c contracts module exists);
///   (b) customer_prices for the exact (party, material);
///   (c) material_price_list at the party's price_tier level, validity /
///       unit / currency filtered;
///   (d) the material card's tier column for the party's price_tier
///       (Retail→price_retail, Wholesale→price_wholesale,
///       Distributor→price_distributor, Special→price_special,
///       Export→price_export, Online→price_online, List→price_list);
///   (e) the material card fallback (price_list, then price_retail).
/// Independently of the price level, the automatic discount is the
/// HIGHEST qualifying discount_rules percentage among the CUSTOMER rule
/// for the party and the MATERIAL rule for the material whose min_qty
/// the line quantity reaches (rules never stack), and BXGY free
/// quantity is floor(qty / bonus_buy_qty) * bonus_qty (migration 099
/// added bonus_buy_qty; bonus_qty/bonus_item_code are from 013).
/// </summary>
public sealed class SalesPriceResolver
{
    private readonly IContractPriceSource _contracts;

    public SalesPriceResolver(IContractPriceSource? contracts = null)
        => _contracts = contracts ?? NullContractPriceSource.Instance;

    /// <summary>The shared resolver, wired (W7c) with the real
    /// contract price source: resolution level (a) now reads
    /// sct_contract_lines of the party's active contracts.</summary>
    public static readonly SalesPriceResolver Default = new(ContractPriceSource.Instance);

    public PriceResolution Resolve(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyKey, string? partyCode, long materialId, string? materialCode,
        decimal qty, string? unit = null, string? currency = null, string? asOfDate = null)
    {
        if (materialId <= 0 && string.IsNullOrWhiteSpace(materialCode))
            return new PriceResolution(0m, PriceSources.None, 0m, 0m, null, null);
        var isPg = IsPg(provider);
        var notDeleted = isPg ? "is_deleted = FALSE" : "is_deleted = 0";
        var keys = CompanyKeys(connection, transaction, provider, companyKey);
        var inList = string.Join(", ", keys.Select((_, i) => $"@co{i}"));
        var date = string.IsNullOrWhiteSpace(asOfDate)
            ? DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : asOfDate.Trim();

        DbCommand Create(string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return command;
        }

        void Add(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        void AddKeys(DbCommand command)
        {
            for (var i = 0; i < keys.Count; i++)
                Add(command, $"@co{i}", keys[i]);
        }

        decimal? Scalar(string sql, Action<DbCommand> bind)
        {
            using var command = Create(sql);
            bind(command);
            var value = command.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        // ---- Material card (levels d/e + limits + bonus + cost) -------
        long matId = materialId;
        string matCode = materialCode?.Trim() ?? string.Empty;
        decimal priceRetail = 0m, priceList = 0m, bonusQty = 0m, bonusBuyQty = 0m;
        string? bonusItemCode = null;
        var tierColumns = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var hasBonusBuy = PartyLedger.ColumnExists(connection, transaction, provider, "materials", "bonus_buy_qty");
        using (var card = Create($"""
            SELECT material_id, code, price_retail, price_wholesale, price_special, price_list,
                   price_export, price_distributor, price_online, bonus_qty,
                   {(hasBonusBuy ? "bonus_buy_qty" : "0")}, bonus_item_code
            FROM materials
            WHERE company_id IN ({inList}) AND {(matId > 0 ? "material_id = @mat" : "code = @matCode")} AND {notDeleted}
            LIMIT 1
            """))
        {
            AddKeys(card);
            if (matId > 0) Add(card, "@mat", matId); else Add(card, "@matCode", matCode);
            using var reader = card.ExecuteReader();
            if (reader.Read())
            {
                matId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                matCode = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? matCode;
                decimal Col(int i) => reader.IsDBNull(i) ? 0m : Convert.ToDecimal(reader.GetValue(i), CultureInfo.InvariantCulture);
                priceRetail = Col(2);
                tierColumns["Retail"] = priceRetail;
                tierColumns["Wholesale"] = Col(3);
                tierColumns["Special"] = Col(4);
                priceList = Col(5);
                tierColumns["List"] = priceList;
                tierColumns["Export"] = Col(6);
                tierColumns["Distributor"] = Col(7);
                tierColumns["Online"] = Col(8);
                bonusQty = Col(9);
                bonusBuyQty = Col(10);
                bonusItemCode = reader.IsDBNull(11) ? null : Convert.ToString(reader.GetValue(11), CultureInfo.InvariantCulture);
            }
        }

        // ---- Party price tier ------------------------------------------
        string? tier = null;
        if (!string.IsNullOrWhiteSpace(partyCode)
            && PartyLedger.TableExists(connection, transaction, provider, "parties"))
        {
            tier = ScalarText(Create, AddKeys,
                $"SELECT price_tier FROM parties WHERE company_id IN ({inList}) AND code = @party AND {notDeleted} LIMIT 1",
                command => Add(command, "@party", partyCode.Trim()));
        }

        decimal? price = null;
        var source = PriceSources.None;

        // (a) Contract hook — no contracts module yet (W7c).
        if (price is null)
        {
            price = _contracts.TryGetPrice(connection, transaction, provider, keys,
                partyCode, matId, matCode, unit, currency, date);
            if (price is not null) source = PriceSources.Contract;
        }

        // (b) customer_prices for the exact party + material.
        if (price is null && !string.IsNullOrWhiteSpace(partyCode) && matCode.Length > 0
            && PartyLedger.TableExists(connection, transaction, provider, "customer_prices"))
        {
            var active = isPg ? "is_active = TRUE" : "is_active = 1";
            price = Scalar(
                $"SELECT price FROM customer_prices WHERE company_id IN ({inList}) AND party_code = @party AND material_code = @matCode AND {active} AND {notDeleted} LIMIT 1",
                command =>
                {
                    AddKeys(command);
                    Add(command, "@party", partyCode.Trim());
                    Add(command, "@matCode", matCode);
                });
            if (price is not null) source = PriceSources.CustomerPrice;
        }

        // (c) material_price_list at the party's tier level.
        if (price is null && !string.IsNullOrWhiteSpace(tier) && matId > 0
            && PartyLedger.TableExists(connection, transaction, provider, "material_price_list"))
        {
            price = Scalar(
                $"""
                SELECT price FROM material_price_list
                WHERE company_id IN ({inList}) AND material_id = @mat AND price_level = @tier
                  AND (valid_from IS NULL OR valid_from = '' OR valid_from <= @date)
                  AND (valid_to IS NULL OR valid_to = '' OR valid_to >= @date)
                  AND (unit_name IS NULL OR unit_name = '' OR unit_name = @unit)
                  AND (currency IS NULL OR currency = '' OR @currency = '' OR currency = @currency)
                ORDER BY valid_from DESC
                LIMIT 1
                """,
                command =>
                {
                    AddKeys(command);
                    Add(command, "@mat", matId);
                    Add(command, "@tier", tier!.Trim());
                    Add(command, "@date", date);
                    Add(command, "@unit", (object?)unit?.Trim() ?? DBNull.Value);
                    Add(command, "@currency", currency?.Trim() ?? string.Empty);
                });
            if (price is not null) source = PriceSources.PriceListTier;
        }

        // (d) The card's tier column for the party's tier.
        if (price is null && !string.IsNullOrWhiteSpace(tier)
            && tierColumns.TryGetValue(tier.Trim(), out var tierPrice) && tierPrice > 0m)
        {
            price = tierPrice;
            source = PriceSources.TierColumn;
        }

        // (e) Material card fallback.
        if (price is null)
        {
            price = priceList > 0m ? priceList : priceRetail;
            source = PriceSources.MaterialCard;
        }

        // ---- Automatic discount: highest qualifying rule, no stacking --
        var discount = 0m;
        if (PartyLedger.TableExists(connection, transaction, provider, "discount_rules"))
        {
            var active = isPg ? "is_active = TRUE" : "is_active = 1";
            using var rules = Create($"""
                SELECT scope, target_code, discount_pct, min_qty FROM discount_rules
                WHERE company_id IN ({inList}) AND {active} AND {notDeleted}
                  AND ((scope = 'CUSTOMER' AND target_code = @party)
                    OR (scope = 'MATERIAL' AND target_code = @matCode))
                """);
            AddKeys(rules);
            Add(rules, "@party", partyCode?.Trim() ?? string.Empty);
            Add(rules, "@matCode", matCode);
            using var reader = rules.ExecuteReader();
            while (reader.Read())
            {
                var pct = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
                var minQty = reader.IsDBNull(3) ? 0m : Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
                if (qty >= minQty && pct > discount)
                    discount = pct;
            }
        }

        // ---- BXGY -------------------------------------------------------
        var freeQty = bonusBuyQty > 0m && bonusQty > 0m && qty >= bonusBuyQty
            ? Math.Floor(qty / bonusBuyQty) * bonusQty
            : 0m;

        return new PriceResolution(
            price ?? 0m, source, discount, freeQty,
            string.IsNullOrWhiteSpace(bonusItemCode) ? null : bonusItemCode.Trim(),
            tier);
    }

    /// <summary>
    /// BXGY line expansion (W7a): for every PAID line, resolves the
    /// material's bonus rule and appends the earned free quantity as a
    /// zero-price companion line (same material, or bonus_item_code's
    /// material when set). A zero-price line moves stock and COGS
    /// through the normal posting paths at zero revenue — verified
    /// against StkPost/StockValuationEngine, which value issues from
    /// the balance, never from the selling price. Pre-existing
    /// zero-price companion lines for a paid material are dropped and
    /// regenerated, so re-saving a reloaded document never doubles the
    /// free quantity. Lines of bonus materials that differ from the
    /// paid material keep their own (zero) price and earn nothing.
    /// </summary>
    public IReadOnlyList<CommercialLineDraft> ExpandBonusLines(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyKey, string? partyCode, IReadOnlyList<CommercialLineDraft> lines)
    {
        var paidMaterials = new HashSet<long>(lines
            .Where(l => l.MaterialId is > 0 && l.UnitPrice > 0m)
            .Select(l => l.MaterialId!.Value));
        var kept = lines
            .Where(l => !(l.UnitPrice == 0m && l.MaterialId is > 0 && paidMaterials.Contains(l.MaterialId.Value)))
            .ToList();
        var result = new List<CommercialLineDraft>(kept);
        foreach (var line in kept)
        {
            if (line.MaterialId is not > 0 || line.UnitPrice <= 0m || line.Qty <= 0m)
                continue;
            var resolution = Resolve(connection, transaction, provider, companyKey,
                partyCode, line.MaterialId.Value, line.MaterialCode, line.Qty, line.Unit);
            if (resolution.BonusFreeQty <= 0m)
                continue;
            long? bonusId = line.MaterialId;
            var bonusCode = line.MaterialCode;
            if (resolution.BonusMaterialCode is { } otherCode
                && !string.Equals(otherCode, line.MaterialCode, StringComparison.OrdinalIgnoreCase))
            {
                bonusId = ResolveMaterialId(connection, transaction, provider, companyKey, otherCode);
                if (bonusId is null)
                    continue; // Bonus material card missing: no free line rather than a broken one.
                bonusCode = otherCode;
            }
            result.Add(new CommercialLineDraft(
                bonusId, bonusCode, $"{line.Description} (Bonus)", line.Unit,
                resolution.BonusFreeQty, 0m, 0m, line.TaxRate, line.WarehouseId, line.SourceLineNo));
        }
        return result;
    }

    private static long? ResolveMaterialId(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyKey, string materialCode)
    {
        var isPg = IsPg(provider);
        var keys = CompanyKeys(connection, transaction, provider, companyKey);
        var inList = string.Join(", ", keys.Select((_, i) => $"@co{i}"));
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT material_id FROM materials WHERE company_id IN ({inList}) AND code = @code AND {(isPg ? "is_deleted = FALSE" : "is_deleted = 0")} LIMIT 1";
        for (var i = 0; i < keys.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@co{i}";
            p.Value = keys[i];
            cmd.Parameters.Add(p);
        }
        var cp = cmd.CreateParameter();
        cp.ParameterName = "@code";
        cp.Value = materialCode;
        cmd.Parameters.Add(cp);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    internal static bool IsPg(string provider)
        => provider.Trim().ToLowerInvariant() is "postgres" or "postgresql" or "npgsql";

    /// <summary>The company's own keys (code + the key the caller passed,
    /// e.g. its Guid form) — the PartyCreditGuard dual-key pattern.</summary>
    internal static List<string> CompanyKeys(
        DbConnection connection, DbTransaction transaction, string provider, string companyKey)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(companyKey))
            return keys;
        var code = PartyLedger.ResolveCompanyCode(connection, transaction, provider, companyKey.Trim());
        if (!string.IsNullOrWhiteSpace(code))
            keys.Add(code);
        if (!keys.Contains(companyKey.Trim()))
            keys.Add(companyKey.Trim());
        return keys;
    }

    private static string? ScalarText(
        Func<string, DbCommand> create, Action<DbCommand> addKeys, string sql, Action<DbCommand> bind)
    {
        using var command = create(sql);
        addKeys(command);
        bind(command);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Minimum-price / margin / discount enforcement (sales cycle W7a, spec
/// §12 hard rule). Runs inside the caller's posting/save transaction at
/// every seam that persists a sales invoice or sales order:
/// CommercialDocumentRepository (chain SAL save + post),
/// OrderDocumentRepository (SO save + post) and InvRepo (native SAL
/// save + post). A material with a limit unset (0/NULL) is never
/// enforced, so pre-W7a documents and fixtures are unaffected.
///
/// Per line, on the NET unit price (unit price after line discount):
///   net &lt; price_min                          → SAL-7018
///   (net − cost) / net &lt; margin_target_pct    → SAL-7019
///   discount_pct &gt; discount_max_pct          → SAL-7020
/// Cost is the valuation fallback chain (balance average, card
/// avg_cost, last purchase, standard — the StockValuationEngine order).
/// Bonus-free lines (unit price 0 by construction) are exempt from the
/// price and margin rules; their cost still posts as COGS at zero
/// revenue through the normal stock path. A breach throws AppError
/// unless the override context carries the matching permission, in
/// which case an spr_price_overrides audit row is written instead.
/// </summary>
public static class SalesPriceGuard
{
    public static void Validate(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyKey, string docKind, string? docNo, long? docId,
        IReadOnlyList<PriceGuardLine> lines, PriceOverrideContext? overrides)
    {
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(companyKey))
            return;
        var isPg = SalesPriceResolver.IsPg(provider);
        var notDeleted = isPg ? "is_deleted = FALSE" : "is_deleted = 0";
        var keys = SalesPriceResolver.CompanyKeys(connection, transaction, provider, companyKey);
        if (keys.Count == 0)
            return;
        var inList = string.Join(", ", keys.Select((_, i) => $"@co{i}"));

        DbCommand Create(string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return command;
        }

        void Add(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        void Audit(PriceGuardLine line, string ruleCode, decimal limit, decimal actual)
        {
            if (!PartyLedger.TableExists(connection, transaction, provider, "spr_price_overrides"))
                return;
            using var audit = Create("""
                INSERT INTO spr_price_overrides
                    (id, company_id, doc_kind, doc_no, doc_id, line_no, material_id, material_code,
                     rule_code, limit_value, actual_value, unit_price, discount_pct, actor)
                VALUES (@id, @co, @dk, @dn, @doc, @line, @mat, @matCode, @rule, @limit, @actual, @price, @disc, @actor)
                """);
            Add(audit, "@id", Guid.NewGuid().ToString("D"));
            Add(audit, "@co", keys[0]);
            Add(audit, "@dk", (object?)docKind ?? DBNull.Value);
            Add(audit, "@dn", (object?)docNo ?? DBNull.Value);
            Add(audit, "@doc", (object?)docId ?? DBNull.Value);
            Add(audit, "@line", line.LineNo);
            Add(audit, "@mat", (object?)line.MaterialId ?? DBNull.Value);
            Add(audit, "@matCode", (object?)line.MaterialCode ?? DBNull.Value);
            Add(audit, "@rule", ruleCode);
            Add(audit, "@limit", limit);
            Add(audit, "@actual", actual);
            Add(audit, "@price", line.UnitPrice);
            Add(audit, "@disc", line.DiscountPct);
            Add(audit, "@actor", overrides!.Actor.Trim());
            audit.ExecuteNonQuery();
        }

        void Breach(PriceGuardLine line, string ruleCode, decimal limit, decimal actual,
            bool priceRule, string message)
        {
            var authorized = overrides is not null
                && !string.IsNullOrWhiteSpace(overrides.Actor)
                && (priceRule ? overrides.PriceOverride : overrides.DiscountOverride);
            if (authorized)
            {
                Audit(line, ruleCode, limit, actual);
                return;
            }
            throw new AppError(ruleCode, message,
                line.MaterialCode ?? line.MaterialId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                limit.ToString(CultureInfo.InvariantCulture),
                actual.ToString(CultureInfo.InvariantCulture));
        }

        // BXGY companion lines: a zero-price line for a material that
        // also has a PAID line in the same document is a bonus-free
        // line by construction (the resolver's free quantity is added
        // as a zero-price line) — exempt from price/margin rules.
        var paidMaterials = new HashSet<long>(lines
            .Where(l => l.MaterialId is > 0 && l.UnitPrice > 0m)
            .Select(l => l.MaterialId!.Value));

        foreach (var line in lines)
        {
            if (line.MaterialId is not > 0)
                continue;
            var isBonusFree = line.IsBonusFree
                || (line.UnitPrice == 0m && paidMaterials.Contains(line.MaterialId.Value));
            decimal priceMin = 0m, marginTarget = 0m, discountMax = 0m;
            decimal avgCost = 0m, lastPurchase = 0m, standardCost = 0m, cardCost = 0m;
            using (var card = Create($"""
                SELECT price_min, margin_target_pct, discount_max_pct, avg_cost, last_purchase_cost, standard_cost, cost
                FROM materials
                WHERE company_id IN ({inList}) AND material_id = @mat AND {notDeleted}
                LIMIT 1
                """))
            {
                for (var i = 0; i < keys.Count; i++)
                    Add(card, $"@co{i}", keys[i]);
                Add(card, "@mat", line.MaterialId.Value);
                using var reader = card.ExecuteReader();
                if (!reader.Read())
                    continue;
                decimal Col(int i) => reader.IsDBNull(i) ? 0m : Convert.ToDecimal(reader.GetValue(i), CultureInfo.InvariantCulture);
                priceMin = Col(0); marginTarget = Col(1); discountMax = Col(2);
                avgCost = Col(3); lastPurchase = Col(4); standardCost = Col(5); cardCost = Col(6);
            }
            if (priceMin <= 0m && marginTarget <= 0m && discountMax <= 0m)
                continue;

            var net = line.UnitPrice * (1m - line.DiscountPct / 100m);

            if (!isBonusFree && priceMin > 0m && net < priceMin)
                Breach(line, ErrorCodes.PriceBelowMinimum, priceMin, net, priceRule: true,
                    $"Line {line.LineNo}: selling price {net} is below the material minimum price {priceMin}.");

            if (!isBonusFree && marginTarget > 0m)
            {
                var cost = CurrentCost(connection, transaction, provider, keys, line.MaterialId.Value,
                    avgCost, lastPurchase, standardCost, cardCost);
                if (cost > 0m)
                {
                    // A zero/negative net price against a real cost is a
                    // -100% (or worse) margin: always below any target.
                    var margin = net > 0m ? (net - cost) / net * 100m : -100m;
                    if (margin < marginTarget)
                        Breach(line, ErrorCodes.MarginBelowMinimum, marginTarget, margin, priceRule: true,
                            $"Line {line.LineNo}: realized margin {margin:0.##}% is below the material target {marginTarget}%.");
                }
            }

            if (discountMax > 0m && line.DiscountPct > discountMax)
                Breach(line, ErrorCodes.DiscountExceedsLimit, discountMax, line.DiscountPct, priceRule: false,
                    $"Line {line.LineNo}: discount {line.DiscountPct}% exceeds the material maximum {discountMax}%.");
        }
    }

    /// <summary>Async wrapper for the async repositories (sync ADO inside
    /// the caller's transaction, the PartyCreditGuard pattern).</summary>
    public static Task ValidateAsync(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyKey, string docKind, string? docNo, long? docId,
        IReadOnlyList<PriceGuardLine> lines, PriceOverrideContext? overrides,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(connection, transaction, provider, companyKey, docKind, docNo, docId, lines, overrides);
        return Task.CompletedTask;
    }

    private static decimal CurrentCost(
        DbConnection connection, DbTransaction transaction, string provider,
        IReadOnlyList<string> keys, long materialId,
        decimal avgCost, decimal lastPurchase, decimal standardCost, decimal cardCost)
    {
        var isPg = SalesPriceResolver.IsPg(provider);
        var notDeleted = isPg ? "is_deleted = FALSE" : "is_deleted = 0";
        var inList = string.Join(", ", keys.Select((_, i) => $"@co{i}"));
        using var balance = connection.CreateCommand();
        balance.Transaction = transaction;
        // The engine's adoption rule: a balance row carrying quantity
        // and an average but a zero stock_value is valued qty x average.
        balance.CommandText = $"""
            SELECT COALESCE(SUM(CASE WHEN stock_value > 0 THEN stock_value ELSE available_qty * average_cost END), 0),
                   COALESCE(SUM(available_qty), 0)
            FROM material_stock_balances
            WHERE company_id IN ({inList}) AND material_id = @mat AND {notDeleted}
            """;
        for (var i = 0; i < keys.Count; i++)
        {
            var p = balance.CreateParameter();
            p.ParameterName = $"@co{i}";
            p.Value = keys[i];
            balance.Parameters.Add(p);
        }
        var mp = balance.CreateParameter();
        mp.ParameterName = "@mat";
        mp.Value = materialId;
        balance.Parameters.Add(mp);
        using var reader = balance.ExecuteReader();
        if (reader.Read())
        {
            var value = reader.IsDBNull(0) ? 0m : Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture);
            var qty = reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (qty > 0m && value > 0m)
                return value / qty;
        }
        if (avgCost > 0m) return avgCost;
        if (lastPurchase > 0m) return lastPurchase;
        if (standardCost > 0m) return standardCost;
        return cardCost;
    }
}

/// <summary>
/// Master price-change audit (W7a): writes spr_price_history rows
/// (old → new, actor, timestamp) for material card prices,
/// material_price_list prices and customer_prices. Called by the save
/// seams that write those masters (MatRepo.Save, DefinitionRepository
/// customer_prices saves); a no-op when the table is absent (pre-099
/// database) or the value did not change.
/// </summary>
public static class SalesPriceHistory
{
    public static void Record(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyKey, string sourceKind, string sourceKey, string fieldName,
        decimal? oldValue, decimal? newValue, string actor)
    {
        if (oldValue == newValue || string.IsNullOrWhiteSpace(actor))
            return;
        if (!PartyLedger.TableExists(connection, transaction, provider, "spr_price_history"))
            return;
        var keys = SalesPriceResolver.CompanyKeys(connection, transaction, provider, companyKey);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO spr_price_history
                (id, company_id, source_kind, source_key, field_name, old_value, new_value, actor)
            VALUES (@id, @co, @kind, @key, @field, @old, @new, @actor)
            """;
        void Add(string name, object value)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            cmd.Parameters.Add(parameter);
        }
        Add("@id", Guid.NewGuid().ToString("D"));
        Add("@co", keys.Count > 0 ? keys[0] : companyKey.Trim());
        Add("@kind", sourceKind);
        Add("@key", sourceKey);
        Add("@field", fieldName);
        Add("@old", (object?)oldValue ?? DBNull.Value);
        Add("@new", (object?)newValue ?? DBNull.Value);
        Add("@actor", actor.Trim());
        cmd.ExecuteNonQuery();
    }
}
