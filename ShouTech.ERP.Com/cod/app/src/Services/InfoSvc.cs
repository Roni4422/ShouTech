using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Configuration;
using ShouTech.Domain;

namespace ShouTech.App.Services;

/// <summary>Entity kinds the global Information Window can describe.</summary>
public enum InfoKind
{
    Material,
    Account,
    Customer,
    Supplier,
    SalesRep,
    Warehouse,
    Document
}

/// <summary>Display format of one information property. The window
/// formats; the service only carries typed values.</summary>
public enum InfoFormat
{
    Text,
    Qty,
    Money,
    /// <summary>Money shown as an absolute value plus its natural
    /// debit/credit side (accounts, party balances).</summary>
    MoneySide,
    Date,
    Percent,
    Count,
    YesNo
}

/// <summary>
/// A reference to the entity the user has selected. Id is the numeric
/// key where the store has one (materials, parties' ledger id,
/// warehouses, documents); GuidId serves the Guid-keyed stores
/// (accounts, sales reps); Code is the human code, always carried for
/// display and for code-keyed resolution; DocKind qualifies Document
/// references (the document kind code, e.g. SAL / PUR).
/// </summary>
public sealed record InfoRef(
    InfoKind Kind,
    long? Id = null,
    string? Code = null,
    string? GuidId = null,
    string? DocKind = null);

/// <summary>
/// The host window's focus around the selection: the party in focus
/// (for last-price-to-this-customer), the material in focus (for a
/// party's last price for it), the warehouse in focus, and a pending
/// stock-out quantity (for the projected balance — e.g. the quantity
/// of the material already on the POS cart being edited).
/// </summary>
public sealed record InfoContext(
    long? PartyLedgerId = null,
    string? PartyKind = null,
    long? MaterialId = null,
    long? WarehouseId = null,
    decimal? PendingOutQty = null);

/// <summary>One resolved fact about an entity. Children carry
/// sub-rows (per-warehouse balances).</summary>
public sealed record InfoProperty(
    string Key,
    InfoFormat Format,
    decimal? Number = null,
    string? Text = null,
    IReadOnlyList<InfoProperty>? Children = null);

/// <summary>The full fact sheet for one entity, in registry order.
/// The window filters it through the user's visible-property set.</summary>
public sealed record InfoSheet(
    InfoKind Kind,
    string Title,
    string? Subtitle,
    IReadOnlyList<InfoProperty> Properties);

/// <summary>
/// Implemented by windows that can feed the Information Window: the
/// entity currently selected in the window, and the focus around it.
/// BaseWin's central F9 chord reads this contract — a window opts in
/// by implementing it, never by wiring its own key handling.
/// </summary>
public interface IInfoContextProvider
{
    InfoRef? CurrentInfoRef { get; }
    InfoContext? CurrentInfoContext { get; }
}

/// <summary>
/// Central selection bus for the Information Window. Provider windows
/// publish on selection change (BaseWin.PublishInfoSelection); the
/// open InfoWin follows. The last published selection is retained so
/// the shell's Information button can reopen the window on it.
/// </summary>
public static class InfoContextHub
{
    public static event Action<InfoRef, InfoContext?>? SelectionPublished;

    public static InfoRef? LastRef { get; private set; }
    public static InfoContext? LastContext { get; private set; }

    public static void Publish(InfoRef reference, InfoContext? context)
    {
        LastRef = reference;
        LastContext = context;
        SelectionPublished?.Invoke(reference, context);
    }
}

/// <summary>
/// The Information Window's resolver (INFO_WINDOW_STUDY): turns an
/// entity reference into an ordered fact sheet. Every value comes
/// from the system's own stores — the engine-maintained
/// material_stock_balances snapshot, party_ledger BASE amounts (the
/// Rpt convention), posted journal lines (the Accounting convention),
/// the document tables, and the entities' own card records. No
/// parallel computation paths and no cached copies.
/// </summary>
public static class InfoSvc
{
    // ------------------------------------------------------------------
    // Property registry: canonical order per kind + default visible set.
    // A property appears in a sheet only when its source exists; the
    // window shows the subset the user checked (WinPref-persisted).
    // ------------------------------------------------------------------

    private static readonly string[] PartyKeys =
    [
        "Code", "Name", "Group", "Balance",
        "CreditLimit", "CreditUsedPct", "CreditDays",
        "OpenAmount", "OldestOpenDate",
        "LastTransactionDate", "LastTransactionDoc", "LastTransactionAmount",
        "LastPriceForMaterial", "PriceTier", "TaxNo",
        "Phone", "Mobile", "Email", "Active"
    ];

    private static readonly IReadOnlyDictionary<InfoKind, string[]> Registry =
        new Dictionary<InfoKind, string[]>
        {
            [InfoKind.Material] =
            [
                "Code", "Name", "Barcode", "Category", "Group", "DefaultUnit",
                "TotalBalance", "ProjectedBalance", "WarehouseBalances", "ReservedQty",
                "StockValue", "AvgCost",
                "SalePrice", "WholesalePrice", "SpecialPrice", "ExportPrice",
                "DistributorPrice", "OnlinePrice", "MinPrice",
                "LastPurchasePrice", "LastPurchaseDate", "LastPurchaseSupplier",
                "LastSalePrice", "LastSaleDate", "LastSalePriceToCustomer",
                "MinSalePrice", "MaxSalePrice", "MinPurchasePrice", "MaxPurchasePrice",
                "AvgSalePrice", "AvgPurchasePrice",
                "CardCost", "StandardCost", "CostMethod",
                "Manufacturer", "Brand", "Origin", "PackQty", "NetWeight", "Size",
                "ServiceFlag", "ReorderPoint", "Notes",
                "SalesThisMonthQty", "SalesThisMonthAmount"
            ],
            [InfoKind.Account] =
            [
                "Code", "Name", "Type", "ParentAccount",
                "DebitTotal", "CreditTotal", "Balance",
                "MovementsCount", "LastMovementDate",
                "EstimatedBudget", "WarningSide", "AccountSide",
                "Currency", "ReconciliationDate", "ClosingAccount",
                "Postable", "Active"
            ],
            [InfoKind.Customer] = PartyKeys,
            [InfoKind.Supplier] = PartyKeys,
            [InfoKind.SalesRep] =
            [
                "Code", "Name", "Group", "Phone", "CommissionPct",
                "Target", "MonthSales", "AchievementPct",
                "MonthCommission", "PendingCommission", "Active"
            ],
            [InfoKind.Warehouse] =
            [
                "Code", "Name", "DefaultFlag", "Active",
                "MaterialsCount", "TotalQty", "StockValue",
                "Address", "City", "Phone", "ParentWarehouse", "Notes"
            ],
            [InfoKind.Document] =
            [
                "DocNo", "DocKind", "DocDate", "Party", "Status",
                "Subtotal", "Discount", "Tax", "GrandTotal",
                "PaidAmount", "OpenAmount", "Currency", "WarehouseName",
                "LinesCount", "SalesRep", "PostedBy", "PostedAt", "CreatedBy"
            ]
        };

    private static readonly IReadOnlyDictionary<InfoKind, string[]> Defaults =
        new Dictionary<InfoKind, string[]>
        {
            // The Phenix checked set (§7 of the evidence): balances,
            // sale price, weighted-average purchase, last purchase,
            // last sale to this customer.
            [InfoKind.Material] =
            [
                "TotalBalance", "ProjectedBalance", "WarehouseBalances",
                "SalePrice", "AvgCost", "LastPurchasePrice", "LastPurchaseDate",
                "LastSalePriceToCustomer"
            ],
            [InfoKind.Account] =
                ["Balance", "DebitTotal", "CreditTotal", "MovementsCount", "LastMovementDate"],
            [InfoKind.Customer] =
                ["Balance", "CreditLimit", "CreditUsedPct", "OpenAmount", "LastTransactionDate", "LastTransactionAmount"],
            [InfoKind.Supplier] =
                ["Balance", "CreditLimit", "CreditUsedPct", "OpenAmount", "LastTransactionDate", "LastTransactionAmount"],
            [InfoKind.SalesRep] =
                ["MonthSales", "Target", "AchievementPct", "MonthCommission", "PendingCommission", "CommissionPct"],
            [InfoKind.Warehouse] =
                ["MaterialsCount", "TotalQty", "StockValue"],
            [InfoKind.Document] =
                ["DocDate", "Party", "Status", "GrandTotal", "PaidAmount", "OpenAmount", "LinesCount"]
        };

    public static IReadOnlyList<string> PropertyKeys(InfoKind kind) => Registry[kind];

    public static IReadOnlyList<string> DefaultVisibleKeys(InfoKind kind) => Defaults[kind];

    // ------------------------------------------------------------------
    // Visible-property configuration (per user + company, WinPref).
    // Stored under InfoWin's own preference scope — the same scope
    // BaseWin builds for the InfoWin window itself.
    // ------------------------------------------------------------------

    private static string PrefScope()
    {
        var user = AppState.CurrentUser?.Username ?? "anonymous";
        var company = AppState.CurrentCompanyCode ?? "no-company";
        return $"{company}/{user}/InfoWin";
    }

    public static List<string> LoadVisibleKeys(InfoKind kind)
    {
        var saved = WinPref.LoadValue(PrefScope(), "Props." + kind);
        if (string.IsNullOrWhiteSpace(saved))
            return new List<string>(Defaults[kind]);
        var known = new HashSet<string>(Registry[kind], StringComparer.Ordinal);
        var keys = saved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(known.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return keys.Count > 0 ? keys : new List<string>(Defaults[kind]);
    }

    public static void SaveVisibleKeys(InfoKind kind, IEnumerable<string> keys)
    {
        var known = new HashSet<string>(Registry[kind], StringComparer.Ordinal);
        var clean = keys.Where(known.Contains).Distinct(StringComparer.Ordinal);
        WinPref.SaveValue(PrefScope(), "Props." + kind, string.Join(",", clean));
    }

    // ------------------------------------------------------------------
    // Resolution
    // ------------------------------------------------------------------

    /// <summary>Resolves the fact sheet for one entity. Throws
    /// AppError INF-8401 when the entity does not exist in the active
    /// company, INF-8402 when the stores cannot be read.</summary>
    public static InfoSheet Resolve(InfoRef reference, InfoContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        try
        {
            return reference.Kind switch
            {
                InfoKind.Material => ResolveMaterial(reference, context),
                InfoKind.Account => ResolveAccount(reference),
                InfoKind.Customer => ResolveParty(reference, "C", context),
                InfoKind.Supplier => ResolveParty(reference, "S", context),
                InfoKind.SalesRep => ResolveSalesRep(reference),
                InfoKind.Warehouse => ResolveWarehouse(reference),
                InfoKind.Document => ResolveDocument(reference),
                _ => throw new AppError(ErrorCodes.InfoEntityNotFound,
                    $"Unsupported information kind '{reference.Kind}'.")
            };
        }
        catch (AppError)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AppError(ErrorCodes.InfoLoadFailed,
                "The information could not be loaded.", ex);
        }
    }

    // ------------------------------------------------------------------
    // Material
    // ------------------------------------------------------------------

    private static InfoSheet ResolveMaterial(InfoRef reference, InfoContext? context)
    {
        if (reference.Id is not long materialId)
            throw NotFound("material");
        var rec = MatRepo.LoadById(materialId, includeChildren: true)
            ?? throw NotFound("material");

        var props = new List<InfoProperty>
        {
            Text("Code", rec.Code),
            Text("Name", DisplayName(rec.Name, rec.NameEn))
        };

        var barcodes = new List<string>();
        foreach (var unit in new[] { rec.Unit1Barcode, rec.Unit2Barcode, rec.Unit3Barcode, rec.Unit4Barcode, rec.Unit5Barcode })
            if (!string.IsNullOrWhiteSpace(unit)) barcodes.Add(unit.Trim());
        barcodes.AddRange(rec.ExtraBarcodes.Select(b => b.Barcode).Where(b => !string.IsNullOrWhiteSpace(b)));
        if (barcodes.Count > 0)
            props.Add(Text("Barcode", string.Join(", ", barcodes.Distinct(StringComparer.OrdinalIgnoreCase))));
        if (!string.IsNullOrWhiteSpace(rec.Category)) props.Add(Text("Category", rec.Category));
        if (!string.IsNullOrWhiteSpace(rec.GroupCode)) props.Add(Text("Group", rec.GroupCode!));
        if (!string.IsNullOrWhiteSpace(rec.DefaultUnit)) props.Add(Text("DefaultUnit", rec.DefaultUnit));

        using var connection = OpenConnection();
        var company = CompanyCode();

        // Balances: the engine-maintained snapshot.
        var balanceRows = Query(connection,
            $"""
            SELECT b.warehouse_id, b.available_qty, b.reserved_qty, b.average_cost,
                   COALESCE(w.warehouse_name_ar, ''), COALESCE(w.warehouse_name_en, '')
            FROM material_stock_balances b
            LEFT JOIN warehouses w
              ON w.company_id = b.company_id AND w.warehouse_id = b.warehouse_id
            WHERE b.company_id = @company AND b.material_id = @material AND b.is_deleted = {NotDeleted}
            ORDER BY b.warehouse_id;
            """,
            ("@company", company), ("@material", materialId));

        decimal total = 0m, reserved = 0m, value = 0m;
        var warehouseProps = new List<InfoProperty>();
        foreach (var row in balanceRows)
        {
            var available = Dec(row[1]);
            total += available;
            reserved += Dec(row[2]);
            value += available * Dec(row[3]);
            warehouseProps.Add(new InfoProperty(
                "Warehouse." + Convert.ToString(row[0], CultureInfo.InvariantCulture),
                InfoFormat.Qty, available,
                DisplayName(Str(row[4]), Str(row[5]))));
        }

        props.Add(new InfoProperty("TotalBalance", InfoFormat.Qty, total));
        if (context?.PendingOutQty is decimal pending)
            props.Add(new InfoProperty("ProjectedBalance", InfoFormat.Qty, total - pending));
        if (warehouseProps.Count > 0)
            props.Add(new InfoProperty("WarehouseBalances", InfoFormat.Qty, total, null, warehouseProps));
        if (reserved != 0m)
            props.Add(new InfoProperty("ReservedQty", InfoFormat.Qty, reserved));
        props.Add(new InfoProperty("StockValue", InfoFormat.Money, value));
        props.Add(new InfoProperty("AvgCost", InfoFormat.Money,
            total != 0m ? value / total : rec.AvgCost));

        // Card prices (the material's own price levels).
        AddMoney(props, "SalePrice", rec.PriceRetail);
        AddMoney(props, "WholesalePrice", rec.PriceWholesale);
        AddMoney(props, "SpecialPrice", rec.PriceSpecial);
        AddMoney(props, "ExportPrice", rec.PriceExport);
        AddMoney(props, "DistributorPrice", rec.PriceDistributor);
        AddMoney(props, "OnlinePrice", rec.PriceOnline);
        AddMoney(props, "MinPrice", rec.PriceMin);

        // Purchase facts from posted purchase lines.
        var lastPurchase = Query(connection,
            $"""
            SELECT l.unit_price, d.doc_date, COALESCE(d.party_name, '')
            FROM purchase_document_lines l
            JOIN purchase_documents d ON d.doc_id = l.doc_id
            WHERE d.company_id = @company AND d.is_deleted = {NotDeleted}
              AND d.status = 'Posted' AND l.material_id = @material
            ORDER BY d.doc_date DESC, d.doc_id DESC LIMIT 1;
            """,
            ("@company", company), ("@material", materialId));
        if (lastPurchase.Count > 0)
        {
            props.Add(new InfoProperty("LastPurchasePrice", InfoFormat.Money, Dec(lastPurchase[0][0])));
            if (Date(lastPurchase[0][1]) is { } date) props.Add(new InfoProperty("LastPurchaseDate", InfoFormat.Date, null, date));
            if (!string.IsNullOrWhiteSpace(Str(lastPurchase[0][2]))) props.Add(Text("LastPurchaseSupplier", Str(lastPurchase[0][2])));
        }

        // Sale facts from posted sales lines (sale kinds only — returns
        // never pollute price statistics).
        var lastSale = Query(connection,
            $"""
            SELECT l.unit_price, d.doc_date
            FROM sales_document_lines l
            JOIN sales_documents d ON d.doc_id = l.doc_id
            WHERE d.company_id = @company AND d.is_deleted = {NotDeleted}
              AND d.status = 'Posted' AND d.doc_kind IN ('SAL', 'POS') AND l.material_id = @material
            ORDER BY d.doc_date DESC, d.doc_id DESC LIMIT 1;
            """,
            ("@company", company), ("@material", materialId));
        if (lastSale.Count > 0)
        {
            props.Add(new InfoProperty("LastSalePrice", InfoFormat.Money, Dec(lastSale[0][0])));
            if (Date(lastSale[0][1]) is { } date) props.Add(new InfoProperty("LastSaleDate", InfoFormat.Date, null, date));
        }

        if (context?.PartyLedgerId is long partyLedgerId && context.PartyKind == "C")
        {
            var toCustomer = Query(connection,
                $"""
                SELECT l.unit_price
                FROM sales_document_lines l
                JOIN sales_documents d ON d.doc_id = l.doc_id
                WHERE d.company_id = @company AND d.is_deleted = {NotDeleted}
                  AND d.status = 'Posted' AND d.doc_kind IN ('SAL', 'POS')
                  AND l.material_id = @material AND d.party_id = @party
                ORDER BY d.doc_date DESC, d.doc_id DESC LIMIT 1;
                """,
                ("@company", company), ("@material", materialId), ("@party", partyLedgerId));
            if (toCustomer.Count > 0)
                props.Add(new InfoProperty("LastSalePriceToCustomer", InfoFormat.Money, Dec(toCustomer[0][0])));
        }

        var saleStats = Query(connection,
            $"""
            SELECT MIN(l.unit_price), MAX(l.unit_price), AVG(l.unit_price)
            FROM sales_document_lines l
            JOIN sales_documents d ON d.doc_id = l.doc_id
            WHERE d.company_id = @company AND d.is_deleted = {NotDeleted}
              AND d.status = 'Posted' AND d.doc_kind IN ('SAL', 'POS') AND l.material_id = @material;
            """,
            ("@company", company), ("@material", materialId));
        if (saleStats.Count > 0 && saleStats[0][0] is not null)
        {
            props.Add(new InfoProperty("MinSalePrice", InfoFormat.Money, Dec(saleStats[0][0])));
            props.Add(new InfoProperty("MaxSalePrice", InfoFormat.Money, Dec(saleStats[0][1])));
            props.Add(new InfoProperty("AvgSalePrice", InfoFormat.Money, Dec(saleStats[0][2])));
        }

        var purchaseStats = Query(connection,
            $"""
            SELECT MIN(l.unit_price), MAX(l.unit_price), AVG(l.unit_price)
            FROM purchase_document_lines l
            JOIN purchase_documents d ON d.doc_id = l.doc_id
            WHERE d.company_id = @company AND d.is_deleted = {NotDeleted}
              AND d.status = 'Posted' AND l.material_id = @material;
            """,
            ("@company", company), ("@material", materialId));
        if (purchaseStats.Count > 0 && purchaseStats[0][0] is not null)
        {
            props.Add(new InfoProperty("MinPurchasePrice", InfoFormat.Money, Dec(purchaseStats[0][0])));
            props.Add(new InfoProperty("MaxPurchasePrice", InfoFormat.Money, Dec(purchaseStats[0][1])));
            props.Add(new InfoProperty("AvgPurchasePrice", InfoFormat.Money, Dec(purchaseStats[0][2])));
        }

        AddMoney(props, "CardCost", rec.Cost);
        AddMoney(props, "StandardCost", rec.StandardCost);
        if (!string.IsNullOrWhiteSpace(rec.CostMethod)) props.Add(Text("CostMethod", rec.CostMethod));
        if (!string.IsNullOrWhiteSpace(rec.Manufacturer)) props.Add(Text("Manufacturer", rec.Manufacturer));
        if (!string.IsNullOrWhiteSpace(rec.Brand)) props.Add(Text("Brand", rec.Brand));
        if (!string.IsNullOrWhiteSpace(rec.OriginCountry)) props.Add(Text("Origin", rec.OriginCountry));
        if (rec.PackQty != 0m) props.Add(new InfoProperty("PackQty", InfoFormat.Qty, rec.PackQty));
        if (rec.WeightNet != 0m) props.Add(new InfoProperty("NetWeight", InfoFormat.Qty, rec.WeightNet));
        if (!string.IsNullOrWhiteSpace(rec.SizeSpec)) props.Add(Text("Size", rec.SizeSpec));
        props.Add(new InfoProperty("ServiceFlag", InfoFormat.YesNo, null, (rec.IsService || !rec.IsStocked) ? "Yes" : "No"));
        if (rec.ReorderQty != 0m) props.Add(new InfoProperty("ReorderPoint", InfoFormat.Qty, rec.ReorderQty));
        if (!string.IsNullOrWhiteSpace(rec.Notes)) props.Add(Text("Notes", rec.Notes));

        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var monthSales = Query(connection,
            $"""
            SELECT COALESCE(SUM(l.qty), 0), COALESCE(SUM(l.line_total), 0)
            FROM sales_document_lines l
            JOIN sales_documents d ON d.doc_id = l.doc_id
            WHERE d.company_id = @company AND d.is_deleted = {NotDeleted}
              AND d.status = 'Posted' AND d.doc_kind IN ('SAL', 'POS')
              AND l.material_id = @material AND d.doc_date >= @from;
            """,
            ("@company", company), ("@material", materialId),
            ("@from", monthStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (monthSales.Count > 0)
        {
            props.Add(new InfoProperty("SalesThisMonthQty", InfoFormat.Qty, Dec(monthSales[0][0])));
            props.Add(new InfoProperty("SalesThisMonthAmount", InfoFormat.Money, Dec(monthSales[0][1])));
        }

        return Sheet(InfoKind.Material, props, rec.Code, DisplayName(rec.Name, rec.NameEn));
    }

    // ------------------------------------------------------------------
    // Account
    // ------------------------------------------------------------------

    private static InfoSheet ResolveAccount(InfoRef reference)
    {
        using var connection = OpenConnection();
        var companyGuid = ResolveCompanyGuid(connection);

        var rows = Query(connection,
            $"""
            SELECT a.account_id, a.account_code, a.account_name, a.account_type,
                   p.account_name, a.is_postable, a.is_active,
                   COALESCE(a.estimated_budget, 0), COALESCE(a.warning_side, 'NONE'),
                   COALESCE(a.account_side, 'BOTH'), a.currency_code,
                   COALESCE(a.exchange_parity, 1), CAST(a.reconciliation_date AS TEXT),
                   cl.account_name
            FROM chart_of_accounts a
            LEFT JOIN chart_of_accounts p ON p.account_id = a.parent_account_id
            LEFT JOIN chart_of_accounts cl ON cl.account_id = a.closing_account_id
            WHERE a.company_id = @company AND a.is_deleted = {NotDeleted}
              AND ((@guid IS NOT NULL AND a.account_id = @guid) OR (@code IS NOT NULL AND a.account_code = @code))
            LIMIT 1;
            """,
            ("@company", companyGuid),
            ("@guid", (object?)reference.GuidId ?? DBNull.Value),
            ("@code", (object?)reference.Code ?? DBNull.Value));
        if (rows.Count == 0)
            throw NotFound("account");
        var row = rows[0];
        var accountId = Str(row[0]);

        var props = new List<InfoProperty>
        {
            Text("Code", Str(row[1])),
            Text("Name", Str(row[2]))
        };
        if (!string.IsNullOrWhiteSpace(Str(row[3]))) props.Add(Text("Type", Str(row[3])));
        if (!string.IsNullOrWhiteSpace(Str(row[4]))) props.Add(Text("ParentAccount", Str(row[4])));

        // Posted-entry sums — the same convention CoaWin displays.
        var sums = Query(connection,
            """
            SELECT COALESCE(SUM(l.debit), 0), COALESCE(SUM(l.credit), 0),
                   COUNT(*), MAX(j.entry_date)
            FROM journal_entry_lines l
            JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            WHERE j.company_id = @company AND j.status = 1 AND l.account_id = @account;
            """,
            ("@company", companyGuid), ("@account", accountId));
        var debit = Dec(sums[0][0]);
        var credit = Dec(sums[0][1]);
        props.Add(new InfoProperty("DebitTotal", InfoFormat.Money, debit));
        props.Add(new InfoProperty("CreditTotal", InfoFormat.Money, credit));
        props.Add(new InfoProperty("Balance", InfoFormat.MoneySide, debit - credit));
        props.Add(new InfoProperty("MovementsCount", InfoFormat.Count, Dec(sums[0][2])));
        if (Date(sums[0][3]) is { } lastMove) props.Add(new InfoProperty("LastMovementDate", InfoFormat.Date, null, lastMove));

        if (Dec(row[7]) != 0m) props.Add(new InfoProperty("EstimatedBudget", InfoFormat.Money, Dec(row[7])));
        if (Str(row[8]) is { Length: > 0 } warning && warning != "NONE") props.Add(Text("WarningSide", warning));
        if (Str(row[9]) is { Length: > 0 } side) props.Add(Text("AccountSide", side));
        if (!string.IsNullOrWhiteSpace(Str(row[10])))
            props.Add(Text("Currency", Dec(row[11]) != 1m
                ? $"{Str(row[10])} × {Dec(row[11]).ToString("0.#####", CultureInfo.InvariantCulture)}"
                : Str(row[10])));
        if (Date(row[12]) is { } recon) props.Add(new InfoProperty("ReconciliationDate", InfoFormat.Date, null, recon));
        if (!string.IsNullOrWhiteSpace(Str(row[13]))) props.Add(Text("ClosingAccount", Str(row[13])));
        props.Add(new InfoProperty("Postable", InfoFormat.YesNo, null, Truthy(row[5]) ? "Yes" : "No"));
        props.Add(new InfoProperty("Active", InfoFormat.YesNo, null, Truthy(row[6]) ? "Yes" : "No"));

        return Sheet(InfoKind.Account, props, Str(row[1]), Str(row[2]));
    }

    // ------------------------------------------------------------------
    // Customer / Supplier
    // ------------------------------------------------------------------

    private static InfoSheet ResolveParty(InfoRef reference, string kind, InfoContext? context)
    {
        using var connection = OpenConnection();
        var company = CompanyCode();

        var rows = Query(connection,
            $"""
            SELECT p.ledger_id, p.code, p.name_en, p.name_ar, p.credit_limit,
                   p.credit_days, p.price_tier, p.tax_no, p.phone, p.mobile,
                   p.email, p.is_active, g.name_ar, g.name_en
            FROM parties p
            LEFT JOIN party_groups g ON g.id = p.party_group_id
            WHERE p.company_id = @company AND p.kind = @kind AND p.is_deleted = {NotDeleted}
              AND ((@id IS NOT NULL AND p.ledger_id = @id) OR (@code IS NOT NULL AND p.code = @code))
            LIMIT 1;
            """,
            ("@company", company), ("@kind", kind),
            ("@id", (object?)reference.Id ?? DBNull.Value),
            ("@code", (object?)reference.Code ?? DBNull.Value));
        if (rows.Count == 0)
            throw NotFound("party");
        var row = rows[0];
        var ledgerId = (long)Dec(row[0]);
        var name = DisplayName(Str(row[3]), Str(row[2]));

        var props = new List<InfoProperty>
        {
            Text("Code", Str(row[1])),
            Text("Name", name)
        };
        var groupName = DisplayName(Str(row[12]), Str(row[13]));
        if (!string.IsNullOrWhiteSpace(groupName)) props.Add(Text("Group", groupName));

        // Balance over BASE amounts (the Rpt convention): positive =
        // the party owes us (customer) / we owe the party (supplier is
        // credit-positive in the ledger, so its balance flips sign to
        // read as an amount outstanding in both cases).
        var balanceRows = Query(connection,
            """
            SELECT COALESCE(SUM(COALESCE(base_debit, debit) - COALESCE(base_credit, credit)), 0)
            FROM party_ledger
            WHERE company_id = @company AND party_kind = @kind AND party_id = @party;
            """,
            ("@company", company), ("@kind", kind), ("@party", ledgerId));
        var rawBalance = Dec(balanceRows[0][0]);
        var balance = kind == "C" ? rawBalance : -rawBalance;
        props.Add(new InfoProperty("Balance", InfoFormat.MoneySide, balance));

        var creditLimit = Dec(row[4]);
        if (creditLimit != 0m)
        {
            props.Add(new InfoProperty("CreditLimit", InfoFormat.Money, creditLimit));
            if (balance > 0m)
                props.Add(new InfoProperty("CreditUsedPct", InfoFormat.Percent, balance / creditLimit * 100m));
        }
        if (Dec(row[5]) != 0m) props.Add(new InfoProperty("CreditDays", InfoFormat.Count, Dec(row[5])));

        // Open amount + oldest open document over posted documents.
        var table = kind == "C" ? "sales_documents" : "purchase_documents";
        var openRows = Query(connection,
            $"""
            SELECT COALESCE(SUM(grand_total - paid_amount), 0), MIN(doc_date)
            FROM {table}
            WHERE company_id = @company AND is_deleted = {NotDeleted}
              AND status = 'Posted' AND party_id = @party AND grand_total > paid_amount;
            """,
            ("@company", company), ("@party", ledgerId));
        props.Add(new InfoProperty("OpenAmount", InfoFormat.Money, Dec(openRows[0][0])));
        if (Date(openRows[0][1]) is { } oldest) props.Add(new InfoProperty("OldestOpenDate", InfoFormat.Date, null, oldest));

        var lastTx = Query(connection,
            """
            SELECT entry_date, COALESCE(doc_no, ''), debit - credit
            FROM party_ledger
            WHERE company_id = @company AND party_kind = @kind AND party_id = @party
            ORDER BY entry_date DESC, ledger_id DESC LIMIT 1;
            """,
            ("@company", company), ("@kind", kind), ("@party", ledgerId));
        if (lastTx.Count > 0)
        {
            if (Date(lastTx[0][0]) is { } txDate) props.Add(new InfoProperty("LastTransactionDate", InfoFormat.Date, null, txDate));
            if (!string.IsNullOrWhiteSpace(Str(lastTx[0][1]))) props.Add(Text("LastTransactionDoc", Str(lastTx[0][1])));
            var amount = Dec(lastTx[0][2]);
            props.Add(new InfoProperty("LastTransactionAmount", InfoFormat.MoneySide, kind == "C" ? amount : -amount));
        }

        // The mirror of the material sheet's last-price-to-customer:
        // when the host window has a material in focus, this party's
        // last transacted price for THAT material.
        if (context?.MaterialId is long contextMaterialId)
        {
            var docTable = kind == "C" ? "sales_documents" : "purchase_documents";
            var lineTable = kind == "C" ? "sales_document_lines" : "purchase_document_lines";
            var priceRows = Query(connection,
                $"""
                SELECT l.unit_price
                FROM {lineTable} l
                JOIN {docTable} d ON d.doc_id = l.doc_id
                WHERE d.company_id = @company AND d.is_deleted = {NotDeleted}
                  AND d.status = 'Posted' AND d.party_id = @party AND l.material_id = @material
                ORDER BY d.doc_date DESC, d.doc_id DESC LIMIT 1;
                """,
                ("@company", company), ("@party", ledgerId), ("@material", contextMaterialId));
            if (priceRows.Count > 0)
                props.Add(new InfoProperty("LastPriceForMaterial", InfoFormat.Money, Dec(priceRows[0][0])));
        }

        if (!string.IsNullOrWhiteSpace(Str(row[6]))) props.Add(Text("PriceTier", Str(row[6])));
        if (!string.IsNullOrWhiteSpace(Str(row[7]))) props.Add(Text("TaxNo", Str(row[7])));
        if (!string.IsNullOrWhiteSpace(Str(row[8]))) props.Add(Text("Phone", Str(row[8])));
        if (!string.IsNullOrWhiteSpace(Str(row[9]))) props.Add(Text("Mobile", Str(row[9])));
        if (!string.IsNullOrWhiteSpace(Str(row[10]))) props.Add(Text("Email", Str(row[10])));
        props.Add(new InfoProperty("Active", InfoFormat.YesNo, null, Truthy(row[11]) ? "Yes" : "No"));

        return Sheet(kind == "C" ? InfoKind.Customer : InfoKind.Supplier, props, Str(row[1]), name);
    }

    // ------------------------------------------------------------------
    // Sales rep
    // ------------------------------------------------------------------

    private static InfoSheet ResolveSalesRep(InfoRef reference)
    {
        using var connection = OpenConnection();
        var company = CompanyCode();

        var rows = Query(connection,
            $"""
            SELECT r.code, r.name_en, r.name_ar, r.phone, r.commission_pct,
                   r.target_amount, r.is_active, g.name_ar, g.name_en
            FROM sales_reps r
            LEFT JOIN sales_rep_groups g ON g.id = r.group_id
            WHERE r.company_id = @company AND r.is_deleted = {NotDeleted}
              AND ((@guid IS NOT NULL AND r.id = @guid) OR (@code IS NOT NULL AND r.code = @code))
            LIMIT 1;
            """,
            ("@company", company),
            ("@guid", (object?)reference.GuidId ?? DBNull.Value),
            ("@code", (object?)reference.Code ?? DBNull.Value));
        if (rows.Count == 0)
            throw NotFound("sales rep");
        var row = rows[0];
        var code = Str(row[0]);
        var name = DisplayName(Str(row[2]), Str(row[1]));

        var props = new List<InfoProperty>
        {
            Text("Code", code),
            Text("Name", name)
        };
        if (!string.IsNullOrWhiteSpace(DisplayName(Str(row[7]), Str(row[8])))) props.Add(Text("Group", DisplayName(Str(row[7]), Str(row[8]))));
        if (!string.IsNullOrWhiteSpace(Str(row[3]))) props.Add(Text("Phone", Str(row[3])));
        props.Add(new InfoProperty("CommissionPct", InfoFormat.Percent, Dec(row[4])));

        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var nextMonth = monthStart.AddMonths(1);
        string Fmt(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Target: the targets table's current monthly period wins; the
        // card's target_amount is the fallback (036 + 031).
        decimal target = 0m;
        var monthly = false;
        var targetRows = Query(connection,
            $"""
            SELECT target_amount, period_kind
            FROM targets
            WHERE company_id = @company AND owner_kind = 'REP' AND owner_code = @code
              AND period_start <= @today
            ORDER BY period_start DESC LIMIT 1;
            """,
            ("@company", company), ("@code", code), ("@today", Fmt(today)));
        if (targetRows.Count > 0)
        {
            target = Dec(targetRows[0][0]);
            monthly = Str(targetRows[0][1]) == "M";
        }
        if (target == 0m)
            target = Dec(row[5]);
        if (target != 0m)
            props.Add(new InfoProperty("Target", InfoFormat.Money, target));

        // Sales + commission from the commission engine's own entries
        // (the system's resolved rep → invoice linkage, 097).
        var monthRows = Query(connection,
            $"""
            SELECT
              COALESCE(SUM(CASE WHEN basis = 'Sales' THEN basis_amount ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN status IN ('Pending', 'Approved') THEN commission_amount ELSE 0 END), 0)
            FROM sco_commission_entries
            WHERE company_id = @company AND rep_code = @code AND is_deleted = {NotDeleted}
              AND period_date >= @from AND period_date < @to;
            """,
            ("@company", company), ("@code", code),
            ("@from", Fmt(monthStart)), ("@to", Fmt(nextMonth)));
        var monthSales = Dec(monthRows[0][0]);
        props.Add(new InfoProperty("MonthSales", InfoFormat.Money, monthSales));
        if ((monthly || targetRows.Count == 0) && target > 0m)
            props.Add(new InfoProperty("AchievementPct", InfoFormat.Percent, monthSales / target * 100m));
        props.Add(new InfoProperty("MonthCommission", InfoFormat.Money, Dec(monthRows[0][1])));

        var pendingRows = Query(connection,
            $"""
            SELECT COALESCE(SUM(commission_amount), 0)
            FROM sco_commission_entries
            WHERE company_id = @company AND rep_code = @code AND is_deleted = {NotDeleted}
              AND status = 'Pending';
            """,
            ("@company", company), ("@code", code));
        props.Add(new InfoProperty("PendingCommission", InfoFormat.Money, Dec(pendingRows[0][0])));
        props.Add(new InfoProperty("Active", InfoFormat.YesNo, null, Truthy(row[6]) ? "Yes" : "No"));

        return Sheet(InfoKind.SalesRep, props, code, name);
    }

    // ------------------------------------------------------------------
    // Warehouse
    // ------------------------------------------------------------------

    private static InfoSheet ResolveWarehouse(InfoRef reference)
    {
        if (reference.Id is not long warehouseId)
            throw NotFound("warehouse");
        using var connection = OpenConnection();
        var company = CompanyCode();

        var rows = Query(connection,
            $"""
            SELECT w.warehouse_code, w.warehouse_name_ar, w.warehouse_name_en,
                   w.is_default, w.is_active, w.address_line1, w.city, w.phone,
                   w.parent_code, w.notes
            FROM warehouses w
            WHERE w.company_id = @company AND w.warehouse_id = @id AND w.is_deleted = {NotDeleted}
            LIMIT 1;
            """,
            ("@company", company), ("@id", warehouseId));
        if (rows.Count == 0)
            throw NotFound("warehouse");
        var row = rows[0];
        var name = DisplayName(Str(row[1]), Str(row[2]));

        var props = new List<InfoProperty>
        {
            Text("Code", Convert.ToString(row[0], CultureInfo.InvariantCulture) ?? string.Empty),
            Text("Name", name),
            new("DefaultFlag", InfoFormat.YesNo, null, Truthy(row[3]) ? "Yes" : "No"),
            new("Active", InfoFormat.YesNo, null, Truthy(row[4]) ? "Yes" : "No")
        };

        var aggregates = Query(connection,
            $"""
            SELECT COUNT(*), COALESCE(SUM(available_qty), 0),
                   COALESCE(SUM(available_qty * average_cost), 0)
            FROM material_stock_balances
            WHERE company_id = @company AND warehouse_id = @id AND is_deleted = {NotDeleted};
            """,
            ("@company", company), ("@id", warehouseId));
        props.Add(new InfoProperty("MaterialsCount", InfoFormat.Count, Dec(aggregates[0][0])));
        props.Add(new InfoProperty("TotalQty", InfoFormat.Qty, Dec(aggregates[0][1])));
        props.Add(new InfoProperty("StockValue", InfoFormat.Money, Dec(aggregates[0][2])));

        if (!string.IsNullOrWhiteSpace(Str(row[5]))) props.Add(Text("Address", Str(row[5])));
        if (!string.IsNullOrWhiteSpace(Str(row[6]))) props.Add(Text("City", Str(row[6])));
        if (!string.IsNullOrWhiteSpace(Str(row[7]))) props.Add(Text("Phone", Str(row[7])));
        if (row[8] is not null && Dec(row[8]) != 0m)
        {
            var parent = Query(connection,
                $"""
                SELECT warehouse_name_ar, warehouse_name_en FROM warehouses
                WHERE company_id = @company AND warehouse_code = @code AND is_deleted = {NotDeleted} LIMIT 1;
                """,
                ("@company", company), ("@code", Dec(row[8])));
            if (parent.Count > 0)
                props.Add(Text("ParentWarehouse", DisplayName(Str(parent[0][0]), Str(parent[0][1]))));
        }
        if (!string.IsNullOrWhiteSpace(Str(row[9]))) props.Add(Text("Notes", Str(row[9])));

        return Sheet(InfoKind.Warehouse, props,
            Convert.ToString(row[0], CultureInfo.InvariantCulture), name);
    }

    // ------------------------------------------------------------------
    // Document
    // ------------------------------------------------------------------

    private static readonly HashSet<string> PurchaseKinds =
        new(StringComparer.OrdinalIgnoreCase) { "PUR", "PRT" };

    private static InfoSheet ResolveDocument(InfoRef reference)
    {
        if (reference.Id is not long docId || string.IsNullOrWhiteSpace(reference.DocKind))
            throw NotFound("document");
        var purchase = PurchaseKinds.Contains(reference.DocKind);
        var table = purchase ? "purchase_documents" : "sales_documents";
        var linesTable = purchase ? "purchase_document_lines" : "sales_document_lines";

        using var connection = OpenConnection();
        var company = CompanyCode();

        var rows = Query(connection,
            $"""
            SELECT d.doc_no, d.doc_kind, d.doc_date, COALESCE(d.party_name, ''),
                   d.status, d.subtotal, d.discount_amount, d.tax_amount,
                   d.grand_total, d.paid_amount, d.currency_code, d.warehouse_id,
                   COALESCE(d.sales_rep_name, ''), COALESCE(d.posted_by, ''),
                   CAST(d.posted_at AS TEXT), COALESCE(d.created_by, '')
            FROM {table} d
            WHERE d.company_id = @company AND d.doc_id = @id AND d.is_deleted = {NotDeleted}
            LIMIT 1;
            """,
            ("@company", company), ("@id", docId));
        if (rows.Count == 0)
            throw NotFound("document");
        var row = rows[0];

        var props = new List<InfoProperty>
        {
            Text("DocNo", Str(row[0])),
            Text("DocKind", Str(row[1]))
        };
        if (Date(row[2]) is { } docDate) props.Add(new InfoProperty("DocDate", InfoFormat.Date, null, docDate));
        if (!string.IsNullOrWhiteSpace(Str(row[3]))) props.Add(Text("Party", Str(row[3])));
        props.Add(Text("Status", Str(row[4])));
        props.Add(new InfoProperty("Subtotal", InfoFormat.Money, Dec(row[5])));
        props.Add(new InfoProperty("Discount", InfoFormat.Money, Dec(row[6])));
        props.Add(new InfoProperty("Tax", InfoFormat.Money, Dec(row[7])));
        props.Add(new InfoProperty("GrandTotal", InfoFormat.Money, Dec(row[8])));
        props.Add(new InfoProperty("PaidAmount", InfoFormat.Money, Dec(row[9])));
        props.Add(new InfoProperty("OpenAmount", InfoFormat.Money, Dec(row[8]) - Dec(row[9])));
        if (!string.IsNullOrWhiteSpace(Str(row[10]))) props.Add(Text("Currency", Str(row[10])));
        if (row[11] is not null)
        {
            var warehouse = Query(connection,
                $"""
                SELECT warehouse_name_ar, warehouse_name_en FROM warehouses
                WHERE company_id = @company AND warehouse_id = @id AND is_deleted = {NotDeleted} LIMIT 1;
                """,
                ("@company", company), ("@id", Dec(row[11])));
            if (warehouse.Count > 0)
                props.Add(Text("WarehouseName", DisplayName(Str(warehouse[0][0]), Str(warehouse[0][1]))));
        }

        var lineCount = Query(connection,
            $"SELECT COUNT(*) FROM {linesTable} WHERE doc_id = @id;",
            ("@id", docId));
        props.Add(new InfoProperty("LinesCount", InfoFormat.Count, Dec(lineCount[0][0])));
        if (!string.IsNullOrWhiteSpace(Str(row[12]))) props.Add(Text("SalesRep", Str(row[12])));
        if (!string.IsNullOrWhiteSpace(Str(row[13]))) props.Add(Text("PostedBy", Str(row[13])));
        if (Date(row[14]) is { } postedAt) props.Add(new InfoProperty("PostedAt", InfoFormat.Date, null, postedAt));
        if (!string.IsNullOrWhiteSpace(Str(row[15]))) props.Add(Text("CreatedBy", Str(row[15])));

        return Sheet(InfoKind.Document, props, Str(row[0]),
            $"{Str(row[1])} · {Str(row[3])}");
    }

    // ------------------------------------------------------------------
    // Plumbing (RecycleBinSvc conventions)
    // ------------------------------------------------------------------

    private static string NotDeleted => DatabaseRuntime.IsPostgreSql() ? "FALSE" : "0";

    private static AppError NotFound(string what)
        => new(ErrorCodes.InfoEntityNotFound, $"The selected {what} was not found.");

    private static InfoSheet Sheet(InfoKind kind, List<InfoProperty> props, string? code, string? name)
    {
        // Registry order is canonical regardless of resolver add order.
        var order = Registry[kind];
        var sorted = props
            .OrderBy(p => Array.IndexOf(order, p.Key))
            .ToList();
        var title = string.IsNullOrWhiteSpace(code)
            ? name ?? string.Empty
            : string.IsNullOrWhiteSpace(name) ? code : $"{code} - {name}";
        return new InfoSheet(kind, title, null, sorted);
    }

    private static InfoProperty Text(string key, string? value)
        => new(key, InfoFormat.Text, null, value);

    private static void AddMoney(List<InfoProperty> props, string key, decimal value)
    {
        if (value != 0m)
            props.Add(new InfoProperty(key, InfoFormat.Money, value));
    }

    private static string DisplayName(string? primary, string? secondary)
        => !string.IsNullOrWhiteSpace(primary) ? primary!.Trim()
            : (secondary ?? string.Empty).Trim();

    private static string CompanyCode()
        => AppState.CurrentCompanyCode?.Trim()
           ?? throw new AppError(ErrorCodes.InfoLoadFailed, "No active company.");

    /// <summary>Chart/journal company key: the companies registry Guid
    /// for the active code, falling back to the code itself (the
    /// Accounting convention for single-tenant layouts).</summary>
    private static string ResolveCompanyGuid(DbConnection connection)
    {
        var code = CompanyCode();
        if (Guid.TryParse(code, out _))
            return code;
        var rows = Query(connection,
            "SELECT company_id FROM companies WHERE company_code = @code LIMIT 1;",
            ("@code", code));
        return rows.Count > 0 ? Str(rows[0][0]) : code;
    }

    private static DbConnection OpenConnection()
    {
        var connectionString = DatabaseRuntime.GetConnectionString()
            ?? throw new AppError(ErrorCodes.InfoLoadFailed, "Database connection is not configured.");
        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static List<object?[]> Query(
        DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var rows = new List<object?[]>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static decimal Dec(object? value)
        => value is null ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static string Str(object? value)
        => value is null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Normalizes a stored date/timestamp to yyyy-MM-dd, or
    /// null when the value is empty or unparseable.</summary>
    private static string? Date(object? value)
    {
        var text = Str(value);
        if (text.Length < 10)
            return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }

    private static bool Truthy(object? value)
    {
        if (value is null) return false;
        if (value is bool b) return b;
        var text = Str(value);
        return text is "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("t", StringComparison.OrdinalIgnoreCase);
    }
}
