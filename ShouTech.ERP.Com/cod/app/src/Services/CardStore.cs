using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Views;

namespace ShouTech.App.Services;

/// <summary>
/// Persistence for the master-data card windows (CstCard, BnkCard, CurCard,
/// RepCard) and the manufacturing order window (MfgWin), backed by the
/// tables created in migration 050 (customers, suppliers, bank_accounts,
/// currencies, currency_rates, sales_reps, sales_rep_targets,
/// manufacturing_orders, manufacturing_order_lines).
///
/// Every write is parameterized and transactional: a card header and its
/// child rows (currency rates, rep targets, manufacturing lines) are saved
/// in ONE transaction, and deletes are soft deletes (is_deleted) so the
/// recycle bin and audit trail keep working. Company scoping follows the
/// rest of the app layer: the active company code from
/// <see cref="AppState.CurrentCompanyCode"/>.
/// </summary>
public static class CardStore
{
    // ------------------------------------------------------------------
    // Records (one per window field set — every UI field has a property)
    // ------------------------------------------------------------------

    public sealed record PartyCard(
        long Id, string Kind, string Code, string NameAr, string NameEn, string Group,
        string TaxNumber, string CommercialReg, decimal CreditLimit, int CreditDays,
        string PriceTier, string GlAccountCode, string Phone, string Mobile,
        string Email, string Address);

    public sealed record BankCardRec(
        long Id, string Code, string Name, string Branch, string AccountNo,
        string Iban, string CurrencyCode, string GlAccountCode);

    public sealed record CurrencyRateRec(
        DateTime Date, decimal Rate, decimal BuyRate, decimal SellRate, string Notes);

    public sealed record CurrencyRec(
        long Id, string Code, string NameAr, string NameEn, string Symbol,
        string FractionName, decimal Rate, decimal MinRate, decimal MaxRate,
        IReadOnlyList<CurrencyRateRec> Rates);

    public sealed record RepTargetRec(
        string Period, decimal Target, decimal ActualSales, decimal CommissionEarned, string Status);

    public sealed record RepRec(
        long Id, string Code, string Name, string Phone, decimal CommissionPercent,
        decimal MonthlyTarget, IReadOnlyList<RepTargetRec> Targets);

    public sealed record MfgLineRec(
        int LineNo, long? MaterialId, string MaterialCode, string MaterialName,
        string UnitCode, decimal Quantity, decimal UnitCost);

    public sealed record MfgOrderRec(
        long Id, string OrderNo, DateTime OrderDate, long? FinishedMaterialId,
        string FinishedMaterialCode, string FinishedMaterialName, decimal Quantity,
        string UnitCode, long? WarehouseId, string WarehouseCode, decimal OverheadCost,
        decimal TotalCost, decimal UnitCost, string Status, IReadOnlyList<MfgLineRec> Lines);

    // ------------------------------------------------------------------
    // Parties (customers / suppliers) — CstCard
    // ------------------------------------------------------------------

    /// <summary>
    /// Loads a party card by code. A code present in both tables yields
    /// Kind="B" (both); customers win the field values in that case.
    /// </summary>
    public static PartyCard? LoadParty(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var cust = LoadPartyFrom("customers", "customer", "C", code.Trim());
        var supp = LoadPartyFrom("suppliers", "supplier", "S", code.Trim());
        if (cust is not null && supp is not null)
            return cust with { Kind = "B" };
        return cust ?? supp;
    }

    private static PartyCard? LoadPartyFrom(string table, string prefix, string kind, string code)
    {
        var cs = ConnectionString();
        if (cs is null) return null;
        using var connection = Open(cs);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {prefix}_id, {prefix}_code, {prefix}_name_ar, {prefix}_name_en,
                   party_group, tax_number, commercial_reg, credit_limit, credit_days,
                   price_tier, gl_account_code, phone, mobile, email, address
            FROM {table}
            WHERE company_id=$c AND {prefix}_code=$code AND is_deleted=0
            LIMIT 1;
            """;
        Add(cmd, "$c", Company());
        Add(cmd, "$code", code);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new PartyCard(
            GetLong(reader, 0), kind, GetText(reader, 1), GetText(reader, 2), GetText(reader, 3),
            GetText(reader, 4), GetText(reader, 5), GetText(reader, 6), GetDec(reader, 7),
            (int)GetDec(reader, 8), GetText(reader, 9), GetText(reader, 10), GetText(reader, 11),
            GetText(reader, 12), GetText(reader, 13), GetText(reader, 14));
    }

    /// <summary>
    /// Upserts the party into the table(s) selected by Kind ("C", "S", "B")
    /// in one transaction. Returns the customers/suppliers id.
    /// </summary>
    public static long SaveParty(PartyCard card, string actor)
    {
        var cs = ConnectionString() ?? throw new InvalidOperationException("No database connection is configured.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        long id = 0;
        if (card.Kind is "C" or "B")
            id = UpsertParty(connection, tx, "customers", "customer", card, actor);
        if (card.Kind is "S" or "B")
            id = UpsertParty(connection, tx, "suppliers", "supplier", card, actor);
        tx.Commit();
        return id;
    }

    private static long UpsertParty(DbConnection connection, DbTransaction tx,
        string table, string prefix, PartyCard card, string actor)
    {
        long? existing = null;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = $"SELECT {prefix}_id FROM {table} WHERE company_id=$c AND {prefix}_code=$code AND is_deleted=0 LIMIT 1;";
            Add(find, "$c", Company());
            Add(find, "$code", card.Code);
            var found = find.ExecuteScalar();
            if (found is not null and not DBNull) existing = Convert.ToInt64(found, CultureInfo.InvariantCulture);
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        if (existing.HasValue)
        {
            cmd.CommandText = $"""
                UPDATE {table} SET
                    {prefix}_name_ar=$nameAr, {prefix}_name_en=$nameEn, party_group=$grp,
                    tax_number=$tax, commercial_reg=$reg, credit_limit=$climit, credit_days=$cdays,
                    price_tier=$tier, gl_account_code=$gl, phone=$phone, mobile=$mobile,
                    email=$email, address=$addr, updated_by=$actor, updated_at={Now()}
                WHERE company_id=$c AND {prefix}_id=$id AND is_deleted=0;
                """;
            Add(cmd, "$id", existing.Value);
        }
        else
        {
            cmd.CommandText = $"""
                INSERT INTO {table}
                    (company_id, {prefix}_code, {prefix}_name_ar, {prefix}_name_en, party_group,
                     tax_number, commercial_reg, credit_limit, credit_days, price_tier,
                     gl_account_code, phone, mobile, email, address, created_by, updated_by)
                VALUES
                    ($c, $code, $nameAr, $nameEn, $grp, $tax, $reg, $climit, $cdays, $tier,
                     $gl, $phone, $mobile, $email, $addr, $actor, $actor)
                RETURNING {prefix}_id;
                """;
            Add(cmd, "$code", card.Code);
        }
        Add(cmd, "$c", Company());
        Add(cmd, "$nameAr", card.NameAr);
        Add(cmd, "$nameEn", card.NameEn);
        Add(cmd, "$grp", card.Group);
        Add(cmd, "$tax", card.TaxNumber);
        Add(cmd, "$reg", card.CommercialReg);
        Add(cmd, "$climit", card.CreditLimit);
        Add(cmd, "$cdays", card.CreditDays);
        Add(cmd, "$tier", card.PriceTier);
        Add(cmd, "$gl", card.GlAccountCode);
        Add(cmd, "$phone", card.Phone);
        Add(cmd, "$mobile", card.Mobile);
        Add(cmd, "$email", card.Email);
        Add(cmd, "$addr", card.Address);
        Add(cmd, "$actor", actor);

        if (existing.HasValue)
        {
            cmd.ExecuteNonQuery();
            return existing.Value;
        }
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    public static bool SoftDeleteParty(string code, string kind, string actor)
    {
        var cs = ConnectionString() ?? throw new InvalidOperationException("No database connection is configured.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        var affected = 0;
        if (kind is "C" or "B")
            affected += SoftDelete(connection, tx, "customers", "customer_code", code, actor);
        if (kind is "S" or "B")
            affected += SoftDelete(connection, tx, "suppliers", "supplier_code", code, actor);
        tx.Commit();
        return affected > 0;
    }

    /// <summary>Total debit/credit for a party from the party ledger (CstCard metrics bar).</summary>
    public static (decimal Debit, decimal Credit) PartyTotals(long partyId, string kind)
    {
        var cs = ConnectionString();
        if (cs is null || partyId <= 0) return (0, 0);
        var ledgerKind = kind == "S" ? "S" : "C";
        using var connection = Open(cs);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(debit), 0), COALESCE(SUM(credit), 0)
            FROM party_ledger
            WHERE company_id=$c AND party_kind=$k AND party_id=$id;
            """;
        Add(cmd, "$c", Company());
        Add(cmd, "$k", ledgerKind);
        Add(cmd, "$id", partyId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (GetDec(reader, 0), GetDec(reader, 1)) : (0, 0);
    }

    // ------------------------------------------------------------------
    // Bank accounts — BnkCard
    // ------------------------------------------------------------------

    public static BankCardRec? LoadBank(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var cs = ConnectionString();
        if (cs is null) return null;
        using var connection = Open(cs);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT bank_id, bank_code, bank_name, branch, account_no, iban, currency_code, gl_account_code
            FROM bank_accounts
            WHERE company_id=$c AND bank_code=$code AND is_deleted=0
            LIMIT 1;
            """;
        Add(cmd, "$c", Company());
        Add(cmd, "$code", code.Trim());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new BankCardRec(GetLong(reader, 0), GetText(reader, 1), GetText(reader, 2),
            GetText(reader, 3), GetText(reader, 4), GetText(reader, 5), GetText(reader, 6), GetText(reader, 7));
    }

    public static long SaveBank(BankCardRec rec, string actor)
    {
        var cs = ConnectionString() ?? throw new InvalidOperationException("No database connection is configured.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        long? existing = FindId(connection, tx, "bank_accounts", "bank_id", "bank_code", rec.Code);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        if (existing.HasValue)
        {
            cmd.CommandText = $"""
                UPDATE bank_accounts SET bank_name=$name, branch=$branch, account_no=$acc,
                    iban=$iban, currency_code=$cur, gl_account_code=$gl,
                    updated_by=$actor, updated_at={Now()}
                WHERE company_id=$c AND bank_id=$id AND is_deleted=0;
                """;
            Add(cmd, "$id", existing.Value);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO bank_accounts
                    (company_id, bank_code, bank_name, branch, account_no, iban, currency_code,
                     gl_account_code, created_by, updated_by)
                VALUES ($c, $code, $name, $branch, $acc, $iban, $cur, $gl, $actor, $actor)
                RETURNING bank_id;
                """;
            Add(cmd, "$code", rec.Code);
        }
        Add(cmd, "$c", Company());
        Add(cmd, "$name", rec.Name);
        Add(cmd, "$branch", rec.Branch);
        Add(cmd, "$acc", rec.AccountNo);
        Add(cmd, "$iban", rec.Iban);
        Add(cmd, "$cur", rec.CurrencyCode);
        Add(cmd, "$gl", rec.GlAccountCode);
        Add(cmd, "$actor", actor);
        long id;
        if (existing.HasValue) { cmd.ExecuteNonQuery(); id = existing.Value; }
        else id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        tx.Commit();
        return id;
    }

    public static bool SoftDeleteBank(string code, string actor)
    {
        var cs = ConnectionString() ?? throw new InvalidOperationException("No database connection is configured.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        var affected = SoftDelete(connection, tx, "bank_accounts", "bank_code", code, actor);
        tx.Commit();
        return affected > 0;
    }

    /// <summary>
    /// Book balance of a bank card = posted journal movement on its linked
    /// GL account (accounts.balance is maintained by the journal engine;
    /// debit/credit columns are the fallback when balance is unused).
    /// </summary>
    public static decimal BankBookBalance(string glAccountCode)
    {
        if (string.IsNullOrWhiteSpace(glAccountCode)) return 0;
        var cs = ConnectionString();
        if (cs is null) return 0;
        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(balance, 0), COALESCE(debit, 0), COALESCE(credit, 0)
                FROM accounts
                WHERE company_id=$c AND account_code=$code AND is_deleted=0
                LIMIT 1;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$code", glAccountCode.Trim());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return 0;
            var balance = GetDec(reader, 0);
            return balance != 0 ? balance : GetDec(reader, 1) - GetDec(reader, 2);
        }
        catch
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------
    // Currencies + rate history — CurCard
    // ------------------------------------------------------------------

    public static CurrencyRec? LoadCurrency(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var cs = ConnectionString();
        if (cs is null) return null;
        using var connection = Open(cs);
        CurrencyRec? rec = null;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT currency_id, currency_code, currency_name_ar, currency_name_en, symbol,
                       fraction_name, exchange_rate, min_rate, max_rate
                FROM currencies
                WHERE company_id=$c AND currency_code=$code AND is_deleted=0
                LIMIT 1;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$code", code.Trim());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            rec = new CurrencyRec(GetLong(reader, 0), GetText(reader, 1), GetText(reader, 2),
                GetText(reader, 3), GetText(reader, 4), GetText(reader, 5), GetDec(reader, 6),
                GetDec(reader, 7), GetDec(reader, 8), Array.Empty<CurrencyRateRec>());
        }

        var rates = new List<CurrencyRateRec>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT rate_date, rate, buy_rate, sell_rate, notes
                FROM currency_rates
                WHERE company_id=$c AND currency_code=$code
                ORDER BY rate_date DESC, rate_id DESC;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$code", code.Trim());
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rates.Add(new CurrencyRateRec(
                    DateTime.TryParse(GetText(reader, 0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today,
                    GetDec(reader, 1), GetDec(reader, 2), GetDec(reader, 3), GetText(reader, 4)));
            }
        }
        return rec with { Rates = rates };
    }

    /// <summary>Saves the currency header and replaces its full rate history in one transaction.</summary>
    public static long SaveCurrency(CurrencyRec rec, string actor)
    {
        var cs = ConnectionString() ?? throw new InvalidOperationException("No database connection is configured.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        long? existing = FindId(connection, tx, "currencies", "currency_id", "currency_code", rec.Code);
        long id;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            if (existing.HasValue)
            {
                cmd.CommandText = $"""
                    UPDATE currencies SET currency_name_ar=$nameAr, currency_name_en=$nameEn,
                        symbol=$sym, fraction_name=$frac, exchange_rate=$rate, min_rate=$min,
                        max_rate=$max, updated_by=$actor, updated_at={Now()}
                    WHERE company_id=$c AND currency_id=$id AND is_deleted=0;
                    """;
                Add(cmd, "$id", existing.Value);
            }
            else
            {
                cmd.CommandText = """
                    INSERT INTO currencies
                        (company_id, currency_code, currency_name_ar, currency_name_en, symbol,
                         fraction_name, exchange_rate, min_rate, max_rate, created_by, updated_by)
                    VALUES ($c, $code, $nameAr, $nameEn, $sym, $frac, $rate, $min, $max, $actor, $actor)
                    RETURNING currency_id;
                    """;
                Add(cmd, "$code", rec.Code);
            }
            Add(cmd, "$c", Company());
            Add(cmd, "$nameAr", rec.NameAr);
            Add(cmd, "$nameEn", rec.NameEn);
            Add(cmd, "$sym", rec.Symbol);
            Add(cmd, "$frac", rec.FractionName);
            Add(cmd, "$rate", rec.Rate);
            Add(cmd, "$min", rec.MinRate);
            Add(cmd, "$max", rec.MaxRate);
            Add(cmd, "$actor", actor);
            if (existing.HasValue) { cmd.ExecuteNonQuery(); id = existing.Value; }
            else id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM currency_rates WHERE company_id=$c AND currency_code=$code;";
            Add(del, "$c", Company());
            Add(del, "$code", rec.Code);
            del.ExecuteNonQuery();
        }
        foreach (var rate in rec.Rates)
        {
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO currency_rates (company_id, currency_code, rate_date, rate, buy_rate, sell_rate, notes, created_by)
                VALUES ($c, $code, $date, $rate, $buy, $sell, $notes, $actor);
                """;
            Add(ins, "$c", Company());
            Add(ins, "$code", rec.Code);
            Add(ins, "$date", rate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(ins, "$rate", rate.Rate);
            Add(ins, "$buy", rate.BuyRate);
            Add(ins, "$sell", rate.SellRate);
            Add(ins, "$notes", rate.Notes);
            Add(ins, "$actor", actor);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return id;
    }

    // ------------------------------------------------------------------
    // Sales representatives + targets — RepCard
    // ------------------------------------------------------------------

    public static RepRec? LoadRep(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var cs = ConnectionString();
        if (cs is null) return null;
        using var connection = Open(cs);
        long repId;
        RepRec rec;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT rep_id, rep_code, rep_name, phone, commission_percent, monthly_target
                FROM sales_reps
                WHERE company_id=$c AND rep_code=$code AND is_deleted=0
                LIMIT 1;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$code", code.Trim());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            repId = GetLong(reader, 0);
            rec = new RepRec(repId, GetText(reader, 1), GetText(reader, 2), GetText(reader, 3),
                GetDec(reader, 4), GetDec(reader, 5), Array.Empty<RepTargetRec>());
        }

        var targets = new List<RepTargetRec>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT period, target_amount, actual_sales, commission_earned, status
                FROM sales_rep_targets
                WHERE company_id=$c AND rep_id=$id
                ORDER BY period DESC;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$id", repId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                targets.Add(new RepTargetRec(GetText(reader, 0), GetDec(reader, 1),
                    GetDec(reader, 2), GetDec(reader, 3), GetText(reader, 4)));
            }
        }
        return rec with { Targets = targets };
    }

    /// <summary>Saves the rep header and upserts every target row in one transaction.</summary>
    public static long SaveRep(RepRec rec, string actor)
    {
        var cs = ConnectionString() ?? throw new InvalidOperationException("No database connection is configured.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        long? existing = FindId(connection, tx, "sales_reps", "rep_id", "rep_code", rec.Code);
        long id;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            if (existing.HasValue)
            {
                cmd.CommandText = $"""
                    UPDATE sales_reps SET rep_name=$name, phone=$phone, commission_percent=$comm,
                        monthly_target=$target, updated_by=$actor, updated_at={Now()}
                    WHERE company_id=$c AND rep_id=$id AND is_deleted=0;
                    """;
                Add(cmd, "$id", existing.Value);
            }
            else
            {
                cmd.CommandText = """
                    INSERT INTO sales_reps
                        (company_id, rep_code, rep_name, phone, commission_percent, monthly_target, created_by, updated_by)
                    VALUES ($c, $code, $name, $phone, $comm, $target, $actor, $actor)
                    RETURNING rep_id;
                    """;
                Add(cmd, "$code", rec.Code);
            }
            Add(cmd, "$c", Company());
            Add(cmd, "$name", rec.Name);
            Add(cmd, "$phone", rec.Phone);
            Add(cmd, "$comm", rec.CommissionPercent);
            Add(cmd, "$target", rec.MonthlyTarget);
            Add(cmd, "$actor", actor);
            if (existing.HasValue) { cmd.ExecuteNonQuery(); id = existing.Value; }
            else id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        foreach (var t in rec.Targets)
        {
            using var up = connection.CreateCommand();
            up.Transaction = tx;
            up.CommandText = $"""
                INSERT INTO sales_rep_targets
                    (company_id, rep_id, period, target_amount, actual_sales, commission_earned, status, created_by, updated_by)
                VALUES ($c, $id, $period, $target, $actual, $comm, $status, $actor, $actor)
                ON CONFLICT (company_id, rep_id, period) DO UPDATE SET
                    target_amount=excluded.target_amount, actual_sales=excluded.actual_sales,
                    commission_earned=excluded.commission_earned, status=excluded.status,
                    updated_by=excluded.updated_by, updated_at={Now()};
                """;
            Add(up, "$c", Company());
            Add(up, "$id", id);
            Add(up, "$period", t.Period);
            Add(up, "$target", t.Target);
            Add(up, "$actual", t.ActualSales);
            Add(up, "$comm", t.CommissionEarned);
            Add(up, "$status", t.Status);
            Add(up, "$actor", actor);
            up.ExecuteNonQuery();
        }
        tx.Commit();
        return id;
    }

    public static bool SoftDeleteRep(string code, string actor)
    {
        var cs = ConnectionString() ?? throw new InvalidOperationException("No database connection is configured.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        var affected = SoftDelete(connection, tx, "sales_reps", "rep_code", code, actor);
        tx.Commit();
        return affected > 0;
    }

    // ------------------------------------------------------------------
    // Manufacturing orders — MfgWin
    // ------------------------------------------------------------------

    public static MfgOrderRec? LoadOrder(string orderNo)
    {
        if (string.IsNullOrWhiteSpace(orderNo)) return null;
        var cs = ConnectionString();
        if (cs is null) return null;
        using var connection = Open(cs);
        MfgOrderRec rec;
        long orderId;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT order_id, order_no, order_date, finished_material_id, finished_material_code,
                       finished_material_name, quantity, unit_code, warehouse_id, warehouse_code,
                       overhead_cost, total_cost, unit_cost, status
                FROM manufacturing_orders
                WHERE company_id=$c AND order_no=$no AND is_deleted=0
                LIMIT 1;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$no", orderNo.Trim());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            orderId = GetLong(reader, 0);
            rec = new MfgOrderRec(orderId, GetText(reader, 1),
                DateTime.TryParse(GetText(reader, 2), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today,
                GetNullableLong(reader, 3), GetText(reader, 4), GetText(reader, 5), GetDec(reader, 6),
                GetText(reader, 7), GetNullableLong(reader, 8), GetText(reader, 9), GetDec(reader, 10),
                GetDec(reader, 11), GetDec(reader, 12), GetText(reader, 13), Array.Empty<MfgLineRec>());
        }

        var lines = new List<MfgLineRec>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT line_no, material_id, material_code, material_name, unit_code, quantity, unit_cost
                FROM manufacturing_order_lines
                WHERE company_id=$c AND order_id=$id
                ORDER BY line_no;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$id", orderId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                lines.Add(new MfgLineRec((int)GetDec(reader, 0), GetNullableLong(reader, 1),
                    GetText(reader, 2), GetText(reader, 3), GetText(reader, 4),
                    GetDec(reader, 5), GetDec(reader, 6)));
            }
        }
        return rec with { Lines = lines };
    }

    /// <summary>
    /// Saves a manufacturing order (header + all lines) in one transaction.
    /// Posted orders are immutable here — Save refuses them so stock
    /// movements can never diverge from the saved lines.
    /// </summary>
    public static long SaveOrder(MfgOrderRec rec, string actor)
    {
        var cs = ConnectionString() ?? throw new InvalidOperationException("No database connection is configured.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        long? existing = FindId(connection, tx, "manufacturing_orders", "order_id", "order_no", rec.OrderNo);
        if (existing.HasValue)
        {
            using var chk = connection.CreateCommand();
            chk.Transaction = tx;
            chk.CommandText = "SELECT status FROM manufacturing_orders WHERE company_id=$c AND order_id=$id;";
            Add(chk, "$c", Company());
            Add(chk, "$id", existing.Value);
            var status = Convert.ToString(chk.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (string.Equals(status, "Posted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A posted manufacturing order cannot be edited.");
        }

        long id;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            if (existing.HasValue)
            {
                cmd.CommandText = $"""
                    UPDATE manufacturing_orders SET order_date=$date, finished_material_id=$fmid,
                        finished_material_code=$fmcode, finished_material_name=$fmname, quantity=$qty,
                        unit_code=$unit, warehouse_id=$whid, warehouse_code=$whcode,
                        overhead_cost=$over, total_cost=$total, unit_cost=$ucost,
                        updated_by=$actor, updated_at={Now()}
                    WHERE company_id=$c AND order_id=$id AND is_deleted=0;
                    """;
                Add(cmd, "$id", existing.Value);
            }
            else
            {
                cmd.CommandText = """
                    INSERT INTO manufacturing_orders
                        (company_id, order_no, order_date, finished_material_id, finished_material_code,
                         finished_material_name, quantity, unit_code, warehouse_id, warehouse_code,
                         overhead_cost, total_cost, unit_cost, status, created_by, updated_by)
                    VALUES ($c, $no, $date, $fmid, $fmcode, $fmname, $qty, $unit, $whid, $whcode,
                            $over, $total, $ucost, 'Draft', $actor, $actor)
                    RETURNING order_id;
                    """;
                Add(cmd, "$no", rec.OrderNo);
            }
            Add(cmd, "$c", Company());
            Add(cmd, "$date", rec.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(cmd, "$fmid", rec.FinishedMaterialId);
            Add(cmd, "$fmcode", rec.FinishedMaterialCode);
            Add(cmd, "$fmname", rec.FinishedMaterialName);
            Add(cmd, "$qty", rec.Quantity);
            Add(cmd, "$unit", rec.UnitCode);
            Add(cmd, "$whid", rec.WarehouseId);
            Add(cmd, "$whcode", rec.WarehouseCode);
            Add(cmd, "$over", rec.OverheadCost);
            Add(cmd, "$total", rec.TotalCost);
            Add(cmd, "$ucost", rec.UnitCost);
            Add(cmd, "$actor", actor);
            if (existing.HasValue) { cmd.ExecuteNonQuery(); id = existing.Value; }
            else id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM manufacturing_order_lines WHERE company_id=$c AND order_id=$id;";
            Add(del, "$c", Company());
            Add(del, "$id", id);
            del.ExecuteNonQuery();
        }
        foreach (var line in rec.Lines)
        {
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO manufacturing_order_lines
                    (company_id, order_id, line_no, material_id, material_code, material_name,
                     unit_code, quantity, unit_cost, total_cost)
                VALUES ($c, $id, $no, $mid, $mcode, $mname, $unit, $qty, $ucost, $total);
                """;
            Add(ins, "$c", Company());
            Add(ins, "$id", id);
            Add(ins, "$no", line.LineNo);
            Add(ins, "$mid", line.MaterialId);
            Add(ins, "$mcode", line.MaterialCode);
            Add(ins, "$mname", line.MaterialName);
            Add(ins, "$unit", line.UnitCode);
            Add(ins, "$qty", line.Quantity);
            Add(ins, "$ucost", line.UnitCost);
            Add(ins, "$total", line.Quantity * line.UnitCost);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return id;
    }

    /// <summary>
    /// Posts a saved draft order in ONE transaction: consumes the raw
    /// materials (qty_out) and receives the finished good (qty_in at the
    /// computed unit cost) in material_stock_movements, updates
    /// material_stock_balances with the same weighted-average rule as
    /// StkPost, then flips the order to Posted.
    /// </summary>
    public static void PostOrder(long orderId, string actor)
    {
        var order = LoadOrderById(orderId)
            ?? throw new InvalidOperationException("The manufacturing order was not found.");
        if (string.Equals(order.Status, "Posted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The manufacturing order is already posted.");
        if (order.WarehouseId is null)
            throw new InvalidOperationException("The order warehouse could not be resolved; posting requires a warehouse.");
        if (order.FinishedMaterialId is null)
            throw new InvalidOperationException("The finished material could not be resolved; posting requires a material card.");

        var cs = ConnectionString() ?? throw new InvalidOperationException("No database connection is configured.");
        using var connection = Open(cs);
        using var tx = connection.BeginTransaction();
        var date = order.OrderDate;

        foreach (var line in order.Lines)
        {
            if (line.MaterialId is null || line.Quantity <= 0) continue;
            InsertMovement(connection, tx, order, line.MaterialId.Value, date,
                "MFG-OUT", qtyIn: 0, qtyOut: line.Quantity, unitCost: line.UnitCost,
                note: line.MaterialName);
            ApplyBalance(connection, tx, order.WarehouseId.Value, line.MaterialId.Value,
                qtyDelta: -line.Quantity, movementCost: line.UnitCost);
        }

        InsertMovement(connection, tx, order, order.FinishedMaterialId.Value, date,
            "MFG-IN", qtyIn: order.Quantity, qtyOut: 0, unitCost: order.UnitCost,
            note: order.FinishedMaterialName);
        ApplyBalance(connection, tx, order.WarehouseId.Value, order.FinishedMaterialId.Value,
            qtyDelta: order.Quantity, movementCost: order.UnitCost);

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE manufacturing_orders SET status='Posted', updated_by=$actor, updated_at={Now()}
                WHERE company_id=$c AND order_id=$id AND is_deleted=0;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$id", orderId);
            Add(cmd, "$actor", actor);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static MfgOrderRec? LoadOrderById(long orderId)
    {
        var cs = ConnectionString();
        if (cs is null) return null;
        using var connection = Open(cs);
        string? orderNo = null;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT order_no FROM manufacturing_orders WHERE company_id=$c AND order_id=$id AND is_deleted=0 LIMIT 1;";
            Add(cmd, "$c", Company());
            Add(cmd, "$id", orderId);
            var value = cmd.ExecuteScalar();
            if (value is not null and not DBNull) orderNo = Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        return orderNo is null ? null : LoadOrder(orderNo);
    }

    private static void InsertMovement(DbConnection connection, DbTransaction tx, MfgOrderRec order,
        long materialId, DateTime date, string type, decimal qtyIn, decimal qtyOut, decimal unitCost, string note)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO material_stock_movements
                (company_id, warehouse_id, material_id, movement_date, movement_type, document_no,
                 qty_in, qty_out, unit_cost, note)
            VALUES ($c, $w, $m, $d, $t, $doc, $qin, $qout, $cost, $note);
            """;
        Add(cmd, "$c", Company());
        Add(cmd, "$w", order.WarehouseId!.Value);
        Add(cmd, "$m", materialId);
        Add(cmd, "$d", DatabaseRuntime.IsPostgreSql()
            ? (object)DateTime.SpecifyKind(date.Date, DateTimeKind.Utc)
            : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(cmd, "$t", type);
        Add(cmd, "$doc", order.OrderNo);
        Add(cmd, "$qin", qtyIn);
        Add(cmd, "$qout", qtyOut);
        Add(cmd, "$cost", unitCost);
        Add(cmd, "$note", note);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Mirrors the StkPost balance rule: inbound quantity re-weights the
    /// average cost; outbound quantity leaves the average unchanged.
    /// </summary>
    private static void ApplyBalance(DbConnection connection, DbTransaction tx,
        long warehouseId, long materialId, decimal qtyDelta, decimal movementCost)
    {
        long? balanceId = null;
        var available = 0m;
        var average = 0m;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT stock_balance_id, available_qty, average_cost
                FROM material_stock_balances
                WHERE company_id=$c AND warehouse_id=$w AND material_id=$m AND is_deleted=0
                LIMIT 1;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$w", warehouseId);
            Add(cmd, "$m", materialId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                balanceId = GetLong(reader, 0);
                available = GetDec(reader, 1);
                average = GetDec(reader, 2);
            }
        }

        var newQty = available + qtyDelta;
        var newAverage = average;
        if (qtyDelta > 0)
        {
            newAverage = newQty > 0
                ? ((available * average) + (qtyDelta * movementCost)) / newQty
                : movementCost;
        }

        using var up = connection.CreateCommand();
        up.Transaction = tx;
        if (balanceId.HasValue)
        {
            up.CommandText = $"""
                UPDATE material_stock_balances
                SET available_qty=$qty, average_cost=$avg, last_cost=$cost, last_updated={Now()}
                WHERE stock_balance_id=$id;
                """;
            Add(up, "$id", balanceId.Value);
        }
        else
        {
            up.CommandText = """
                INSERT INTO material_stock_balances
                    (company_id, warehouse_id, material_id, available_qty, reserved_qty, average_cost, last_cost)
                VALUES ($c, $w, $m, $qty, 0, $avg, $cost);
                """;
            Add(up, "$c", Company());
            Add(up, "$w", warehouseId);
            Add(up, "$m", materialId);
        }
        Add(up, "$qty", newQty);
        Add(up, "$avg", newAverage);
        Add(up, "$cost", movementCost);
        up.ExecuteNonQuery();
    }

    /// <summary>Resolves a material code to (id, name) in the active company; null when unknown.</summary>
    public static (long Id, string Name)? ResolveMaterial(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var cs = ConnectionString();
        if (cs is null) return null;
        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT material_id, name_ar FROM materials
                WHERE company_id=$c AND code=$code AND is_deleted=0
                LIMIT 1;
                """;
            Add(cmd, "$c", Company());
            Add(cmd, "$code", code.Trim());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (GetLong(reader, 0), GetText(reader, 1));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a warehouse by its numeric code text (warehouses.warehouse_code)
    /// or by exact name; null when unknown.
    /// </summary>
    public static long? ResolveWarehouseId(string codeOrName)
    {
        if (string.IsNullOrWhiteSpace(codeOrName)) return null;
        var cs = ConnectionString();
        if (cs is null) return null;
        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            if (long.TryParse(codeOrName.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                cmd.CommandText = """
                    SELECT warehouse_id FROM warehouses
                    WHERE company_id=$c AND warehouse_code=$code AND is_deleted=0
                    LIMIT 1;
                    """;
                Add(cmd, "$code", numeric);
            }
            else
            {
                cmd.CommandText = """
                    SELECT warehouse_id FROM warehouses
                    WHERE company_id=$c AND (warehouse_name_ar=$code OR warehouse_name_en=$code) AND is_deleted=0
                    LIMIT 1;
                    """;
                Add(cmd, "$code", codeOrName.Trim());
            }
            Add(cmd, "$c", Company());
            var value = cmd.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Shared helpers (TaxSvc/RptQuery conventions)
    // ------------------------------------------------------------------

    private static string? ConnectionString()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        return string.IsNullOrWhiteSpace(cs) ? null : cs;
    }

    private static string Company() => AppState.CurrentCompanyCode ?? string.Empty;

    private static string Now() => DatabaseRuntime.IsPostgreSql() ? "now()" : "datetime('now')";

    private static DbConnection Open(string connectionString)
    {
        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
        connection.Open();
        return connection;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }

    private static long? FindId(DbConnection connection, DbTransaction tx,
        string table, string idColumn, string codeColumn, string code)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {idColumn} FROM {table} WHERE company_id=$c AND {codeColumn}=$code AND is_deleted=0 LIMIT 1;";
        Add(cmd, "$c", Company());
        Add(cmd, "$code", code);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static int SoftDelete(DbConnection connection, DbTransaction tx,
        string table, string codeColumn, string code, string actor)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE {table} SET is_deleted=1, deleted_at={Now()}, deleted_by=$actor,
                updated_by=$actor, updated_at={Now()}
            WHERE company_id=$c AND {codeColumn}=$code AND is_deleted=0;
            """;
        Add(cmd, "$c", Company());
        Add(cmd, "$code", code.Trim());
        Add(cmd, "$actor", actor);
        return cmd.ExecuteNonQuery();
    }

    private static string GetText(DbDataReader reader, int i)
        => reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;

    private static decimal GetDec(DbDataReader reader, int i)
        => reader.IsDBNull(i) ? 0m : Convert.ToDecimal(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static long GetLong(DbDataReader reader, int i)
        => reader.IsDBNull(i) ? 0 : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static long? GetNullableLong(DbDataReader reader, int i)
        => reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);
}
