using System.Data.Common;
using System.IO.Compression;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting;

using ShouTech.Domain;
namespace ShouTech.App.Services;

/// <summary>A user-facing export item backed by a known business table.</summary>
public sealed record DataExportOption(
    string Key,
    string DisplayName,
    string TableName,
    DataExportFilter Filter = DataExportFilter.None,
    bool IsSystemInfo = false,
    DatasetImportPolicy Policy = DatasetImportPolicy.Master);

/// <summary>
/// How a dataset may be imported (Step 10). Export is always allowed
/// (subject to DATA.EXPORT at the window); import is classified because
/// a spreadsheet upsert bypasses every posting engine:
/// <list type="bullet">
/// <item><see cref="Master"/> — master data: Excel upsert allowed, but
/// live balance columns are stripped and reported, never written.</item>
/// <item><see cref="PackageOnly"/> — transactional data (documents,
/// journal entries, movements, cheques, production orders): Excel
/// import refused with DATA-4006; exchange happens through the .stei
/// dataset package, whose insert-ignore semantics never overwrite.</item>
/// <item><see cref="EngineOwned"/> — balance/layer state owned by the
/// stock valuation engine: Excel import refused with DATA-4005;
/// balances change only through posted movements. A .stei package
/// restore (insert-ignore) remains allowed.</item>
/// </list>
/// </summary>
public enum DatasetImportPolicy
{
    Master,
    PackageOnly,
    EngineOwned
}

public enum DataExportFilter
{
    None,
    Sales,
    Returns,
    OpeningStock,
    CustomerParties,
    SupplierParties,
    CustomerGroups,
    SupplierGroups,
    SalesDocuments,
    SalesReturnDocuments,
    PurchaseDocuments,
    PurchaseReturnDocuments,
    SalesLines,
    SalesReturnLines,
    PurchaseLines,
    PurchaseReturnLines,
    JournalEntryLines,
    ProductionOrderLines
}

/// <summary>
/// Result of an Excel dataset import.
/// Upsert mode only writes rows present in the file.
/// Replace mode also removes (soft-delete when available) company rows absent from the file.
/// </summary>
public sealed record ExcelImportResult(
    int UpsertedRows,
    int DeletedRows,
    bool ReplaceMode,
    string DatasetKey,
    string TableName)
{
    public int DataRows { get; init; }
    public IReadOnlyList<string> MatchedColumns { get; init; } = [];
    public IReadOnlyList<string> SkippedColumns { get; init; } = [];
    public bool FollowedWorkbookIdentity { get; init; }
    /// <summary>
    /// Balance columns present in the workbook but deliberately NOT
    /// written (Step 10): live balances change only through posting.
    /// </summary>
    public IReadOnlyList<string> RejectedBalanceColumns { get; init; } = [];
}

/// <summary>Read-only look at an .xlsx before any database write.</summary>
public sealed record ExcelImportPreview(
    bool HasIdentityMarker,
    string DatasetKey,
    string TableName,
    int DataRows,
    IReadOnlyList<string> MatchedColumns,
    IReadOnlyList<string> SkippedColumns,
    bool DatasetDiffersFromSelection);


/// <summary>
/// Exports curated business datasets to the report service's Excel and PDF
/// writers. Table names are selected only from the static catalog below.
/// </summary>
public static class DataExportSvc
{
    private const string LogCategory = "DataExchange";

    private static void LogInfo(string message)
    {
        try { AppServiceHost.Logging.LogInfo(LogCategory, message); } catch (Exception exL72) {
            AppHost.Errors.Report(ErrorCodes.ExportFailed, exL72, "DataExportSvc.LogInfo"); }
    }

    private static void LogWarn(string message)
    {
        try { AppServiceHost.Logging.LogWarning(LogCategory, message); } catch (Exception exL77) {
            AppHost.Errors.Report(ErrorCodes.ExportFailed, exL77, "DataExportSvc.LogWarn"); }
    }

    private static void LogError(string message, Exception? ex = null)
    {
        try
        {
            if (ex != null) AppServiceHost.Logging.LogError(LogCategory, message, ex);
            else AppServiceHost.Logging.LogError(LogCategory, message);
        }
        catch (Exception exL87)
        {
            AppHost.Errors.Report(ErrorCodes.ExportFailed, exL87, "DataExportSvc.LogError");
        }
    }

    private static void LogOp(string action, string message)
    {
        try { AppServiceHost.Logging.LogOperation(LogCategory, action, message); } catch (Exception exL92) {
            AppHost.Errors.Report(ErrorCodes.ExportFailed, exL92, "DataExportSvc.LogOp"); }
    }

    private sealed record DatasetIdentity(
        int Version,
        string Key,
        string TableName,
        string[] Headers,
        string[] Fields,
        string[] Types);

    private sealed record ExcelSheet(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string?>> Rows);

    private sealed record DatasetSeed(
        string Key,
        string EnglishName,
        string[] TableCandidates,
        DataExportFilter Filter = DataExportFilter.None,
        DatasetImportPolicy Policy = DatasetImportPolicy.Master);

    // Step 10 catalog repair (sweep DX-02): every seed below resolves to
    // a table that EXISTS in the current schema (post Steps 2/4/8) and
    // carries a company scope — its own company_id column, a parent
    // document join for line tables, or the companies row itself.
    // Removed seeds (table does not exist; see docs/FIX_PHASE_STEP1.md
    // for the per-seed reconciliation): Branches, AccountTypes,
    // ProductClassAttributes, ProductUnits, CustomerContacts,
    // CustomerAddresses, SupplierContacts, Employees, Departments,
    // JobTitles, WarehouseLocations, WarehouseZones, WarehouseTypes,
    // TaxCodes, StockAdjustments, StockAdjustmentLines,
    // StockReservations, Batches, SerialNumbers, ValuationMethods,
    // ProductValuation, ValuationHistory, WorkCenters, ReceiptVouchers,
    // PaymentVouchers, PosSessions — and BankAccounts (Step 8).
    // Customers/Suppliers (and their groups) now read the ONE canonical
    // party master (parties / party_groups, Step 4) split by kind;
    // documents read the real document tables (sales_documents /
    // purchase_documents) split by doc_kind; production orders read the
    // repository-canonical mfg_orders family.
    private static readonly DatasetSeed[] Catalog =
    [
        new("Company", "Company data", ["companies"], Policy: DatasetImportPolicy.PackageOnly),
        new("Accounts", "Chart of accounts", ["chart_of_accounts"]),
        new("Products", "Product cards and directory", ["materials"]),
        new("ProductClasses", "Product classes", ["product_categories"]),
        new("Brands", "Brands", ["brands"]),
        new("ProductAttributes", "Product attributes", ["material_attributes"]),
        new("ProductSuppliers", "Product suppliers", ["material_suppliers"]),
        new("ProductBarcodes", "Product barcodes", ["material_extra_barcodes"]),
        new("Customers", "Customer cards", ["parties"], DataExportFilter.CustomerParties),
        new("CustomerGroups", "Customer groups", ["party_groups"], DataExportFilter.CustomerGroups),
        new("Suppliers", "Supplier cards", ["parties"], DataExportFilter.SupplierParties),
        new("SupplierGroups", "Supplier groups", ["party_groups"], DataExportFilter.SupplierGroups),
        new("SalesRepresentatives", "Sales representatives", ["sales_reps"]),
        new("SalesRepresentativeTargets", "Sales representative targets", ["sales_rep_targets"]),
        new("Warehouses", "Warehouses", ["warehouses"]),
        new("Units", "Units of measure", ["units_of_measure"]),
        new("FiscalYears", "Fiscal years", ["fiscal_years"]),
        new("Currencies", "Currencies and rates", ["currencies"]),
        new("CurrencyRates", "Currency exchange rates", ["currency_rates"]),
        new("Banks", "Banks and bank accounts", ["banks"]),
        new("Sales", "Sales invoices", ["sales_documents"], DataExportFilter.SalesDocuments, DatasetImportPolicy.PackageOnly),
        new("SalesLines", "Sales invoice details", ["sales_document_lines"], DataExportFilter.SalesLines, DatasetImportPolicy.PackageOnly),
        new("SalesReturns", "Sales returns", ["sales_documents"], DataExportFilter.SalesReturnDocuments, DatasetImportPolicy.PackageOnly),
        new("SalesReturnLines", "Sales return details", ["sales_document_lines"], DataExportFilter.SalesReturnLines, DatasetImportPolicy.PackageOnly),
        new("Purchases", "Purchase invoices", ["purchase_documents"], DataExportFilter.PurchaseDocuments, DatasetImportPolicy.PackageOnly),
        new("PurchaseLines", "Purchase invoice details", ["purchase_document_lines"], DataExportFilter.PurchaseLines, DatasetImportPolicy.PackageOnly),
        new("PurchaseReturns", "Purchase returns", ["purchase_documents"], DataExportFilter.PurchaseReturnDocuments, DatasetImportPolicy.PackageOnly),
        new("PurchaseReturnLines", "Purchase return details", ["purchase_document_lines"], DataExportFilter.PurchaseReturnLines, DatasetImportPolicy.PackageOnly),
        new("OpeningStock", "Opening stock and balances", ["material_stock_balances"], Policy: DatasetImportPolicy.EngineOwned),
        new("StockMovements", "Inventory movements", ["material_stock_movements"], Policy: DatasetImportPolicy.PackageOnly),
        new("CostLayers", "Inventory cost layers", ["stock_cost_layers"], Policy: DatasetImportPolicy.EngineOwned),
        new("ProductionOrders", "Production orders", ["mfg_orders"], Policy: DatasetImportPolicy.PackageOnly),
        new("ProductionOrderLines", "Production order details", ["mfg_order_lines"], DataExportFilter.ProductionOrderLines, DatasetImportPolicy.PackageOnly),
        new("PosTerminals", "POS terminals", ["pos_terminals"]),
        new("JournalEntries", "Journal entries", ["journal_entries"], Policy: DatasetImportPolicy.PackageOnly),
        new("JournalEntryLines", "Journal entry details", ["journal_entry_lines"], DataExportFilter.JournalEntryLines, DatasetImportPolicy.PackageOnly),
        new("Cheques", "Cheques", ["cheques"], Policy: DatasetImportPolicy.PackageOnly)
    ];

    /// <summary>
    /// Live balance columns per table (Step 10): values the posting /
    /// valuation engines own. An Excel import of a Master dataset has
    /// these columns stripped and reported — the workbook may carry
    /// them (exports do), but importing never writes them, so a stale
    /// spreadsheet cannot overwrite a live balance (sweep DX-04).
    /// chart_of_accounts needs no entry: migration 059 dropped the
    /// cached balance/debit/credit columns — balances are computed
    /// from posted journal lines and exist nowhere as stored columns.
    /// </summary>
    private static readonly Dictionary<string, string[]> BalanceColumnsByTable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["materials"] = ["qty_on_hand", "avg_cost"],
            ["sales_rep_targets"] = ["actual_sales", "commission_earned"]
        };

    /// <summary>
    /// Tables whose company_id stores the company's internal Guid
    /// (companies.company_id) — exactly the catalog tables whose schema
    /// declares FOREIGN KEY (company_id) REFERENCES companies. Every
    /// other catalog table stores the company CODE. Export scoping
    /// matches BOTH keys, so it is correct for either family; import
    /// forcing must pick the family's own shape (Step 9 dual-key rule).
    /// </summary>
    private static readonly HashSet<string> GuidCompanyTables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "chart_of_accounts", "fiscal_years", "brands", "product_categories", "units_of_measure"
        };

    /// <summary>Parent scope for line tables that carry no company_id of their own.</summary>
    private sealed record ParentScope(string ParentTable, string ParentKey, string ChildKey);

    private static readonly Dictionary<string, ParentScope> ParentScopeByTable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sales_document_lines"] = new("sales_documents", "doc_id", "doc_id"),
            ["purchase_document_lines"] = new("purchase_documents", "doc_id", "doc_id"),
            ["journal_entry_lines"] = new("journal_entries", "journal_entry_id", "journal_entry_id"),
            ["mfg_order_lines"] = new("mfg_orders", "id", "order_id")
        };

    private static readonly string[] MaterialCardExportFields =
    [
        "material_id", "code", "name", "group_code", "item_type", "category",
        "brand", "model", "color", "size_spec", "manufacturer", "origin_country", "source",
        "alt_code", "gtin", "supplier_code", "abc_class", "status", "is_stocked", "is_purchased",
        "is_sold", "is_produced", "is_service", "notes", "unit1", "unit1_factor", "unit1_barcode",
        "unit2", "unit2_factor", "unit2_barcode", "unit3", "unit3_factor", "unit3_barcode",
        "unit4", "unit4_factor", "unit4_barcode", "unit5", "unit5_factor", "unit5_barcode",
        "default_unit", "purchase_unit", "sales_unit", "cost_method", "cost", "price_retail",
        "price_wholesale", "price_special", "price_min", "price_list", "price_export",
        "price_distributor", "price_online", "price_currency", "allow_invoice_disc",
        "price_includes_tax", "qty_on_hand", "min_qty", "max_qty", "reorder_qty", "safety_stock",
        "default_warehouse", "shelf_bin", "lead_time_days", "has_expiry", "shelf_life_days",
        "has_serial", "has_batch", "is_taxable", "tax_rate", "tax_code", "acc_inventory",
        "acc_sales", "acc_sales_return", "acc_cogs", "acc_purchase", "acc_purchase_return",
        "acc_expense", "acc_wip", "allow_sale", "allow_purchase", "allow_return",
        "allow_negative", "image_path", "description"
    ];

    public static async Task<IReadOnlyList<DataExportOption>> GetAvailableOptionsAsync(CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var tables = await GetTableNamesAsync(connection, ct);
        var options = new List<DataExportOption>();

        foreach (var seed in Catalog)
        {
            var table = ResolveTableName(tables, seed.TableCandidates);
            if (table == null)
                continue;

            var display = AppServiceHost.Localization.GetString(
                $"DataExchange.Datasets.{seed.Key}", seed.EnglishName);
            options.Add(new DataExportOption(seed.Key, display, table, seed.Filter, Policy: seed.Policy));
        }

        options.Insert(0, new DataExportOption(
            "SystemInfo",
            AppServiceHost.Localization.GetString("DataExchange.Datasets.SystemInfo", "Company, database and backup information"),
            string.Empty,
            IsSystemInfo: true));
        return options;
    }

    internal static string? ResolveTableName(
        IEnumerable<string> tableNames,
        IEnumerable<string> tableCandidates)
    {
        ArgumentNullException.ThrowIfNull(tableNames);
        ArgumentNullException.ThrowIfNull(tableCandidates);
        var available = tableNames.ToArray();
        foreach (var candidate in tableCandidates)
        {
            var actualName = available.FirstOrDefault(name =>
                string.Equals(name, candidate, StringComparison.Ordinal));
            actualName ??= available.FirstOrDefault(name =>
                string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
            if (actualName != null)
                return actualName;
        }
        return null;
    }

    public static async Task<XptServ.ExportModel> BuildExportModelAsync(
        DataExportOption option,
        string companyCode,
        string companyName,
        string generatedBy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (option.IsSystemInfo)
            return BuildSystemInfoModel(option, companyCode, companyName, generatedBy);

        if (!Catalog.Any(x => x.Key == option.Key && x.Filter == option.Filter &&
            x.TableCandidates.Contains(option.TableName, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The selected dataset is not in the export catalog.");

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        // Phase 4: defense in depth — beyond the static catalog check above, the
        // table must exist in the live schema before its name is interpolated.
        await SqlIdentifier.RequireTableAsync(connection, option.TableName, ct);
        // Step 10 (DX-01): EVERY dataset export is scoped to the active
        // company. The scope matches the company's OWN keys (its code and
        // its internal Guid — the schema's dual-key reality, Steps 4/9),
        // never another company's, and fails closed when the company
        // cannot be resolved: an unscoped SELECT * is not a fallback.
        var tableColumns = await GetColumnNamesAsync(connection, option.TableName, ct);
        // Use declared database column types for the workbook identity. Runtime
        // values are not a schema: SQLite may return Int64 for a NUMERIC/REAL
        // column when a particular row happens to contain an integral value.
        // Deriving the import type from that runtime value mislabels decimal
        // fields as WholeNumber and makes a valid value such as 1.6 fail on
        // re-import.
        var declaredColumnTypes = await GetColumnTypeMapAsync(connection, option.TableName, ct);
        var (sql, scopeParameters) = await BuildScopedExportQueryAsync(connection, option, tableColumns, companyCode, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in scopeParameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync(ct);

        var fields = Enumerable.Range(0, reader.FieldCount)
            .Where(i => reader.GetFieldType(i) != typeof(byte[]))
            .Select(i => new { Index = i, Name = reader.GetName(i), Type = reader.GetFieldType(i) })
            .ToList();
        var fieldsByName = fields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        if (IsMaterialsTable(option.TableName) && !fieldsByName.ContainsKey("name"))
            throw new InvalidOperationException(
                "Material cards cannot be exported because the active materials table is missing its required language-neutral name column. Recreate or migrate the company database before exporting.");
        fields = GetMaterialCardExportFieldNames(option.TableName, fieldsByName.Keys)
            .Select(name => fieldsByName[name])
            .ToList();
        if (fields.Count == 0)
            throw new InvalidOperationException("This dataset does not contain exportable columns.");

        var columnKeys = fields.Select((f, i) => $"Column{i + 1}").ToArray();
        var columns = fields.Select((field, i) => new RptCol
        {
            Key = columnKeys[i],
            HeaderTextKey = field.Name,
            Type = declaredColumnTypes.TryGetValue(field.Name, out var declaredType) &&
                Enum.TryParse<RptColType>(declaredType, out var schemaType)
                    ? schemaType
                    : GetColumnType(field.Name, field.Type),
            Width = 140
        }).ToArray();
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        var categoryCards = IsMaterialsTable(option.TableName) ? LoadCategoryCards() : new List<MatRepo.MatGroupRec>();
        var categoryColumnKey = IsMaterialsTable(option.TableName)
            ? Enumerable.Range(0, fields.Count)
                .Where(i => fields[i].Name.Equals("category", StringComparison.OrdinalIgnoreCase))
                .Select(i => columnKeys[i]).FirstOrDefault()
            : null;

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < fields.Count; i++)
            {
                var value = reader.GetValue(fields[i].Index);
                row[columnKeys[i]] = value is DBNull ? null : value;
            }
            // Product cards: show the category NAME, not the stored card code.
            if (categoryColumnKey != null && row[categoryColumnKey] is string categoryCode)
                row[categoryColumnKey] = CategoryCodeToName(categoryCode, categoryCards);
            rows.Add(row);
        }

        if (rows.Count == 0)
            throw new InvalidOperationException("No records were found for the selected dataset and company.");

        var translatedHeaders = columns.ToDictionary(
            c => c.HeaderTextKey,
            c => AppServiceHost.Localization.GetString(
                $"DataExchange.Columns.{ToSnakeCase(c.HeaderTextKey)}", Humanize(c.HeaderTextKey)),
            StringComparer.Ordinal);

        return new XptServ.ExportModel
        {
            ReportKey = $"DataExport.{option.Key}",
            Title = option.DisplayName,
            CompanyName = companyName,
            GeneratedBy = generatedBy,
            FilterSummary =
                $"{rows.Count.ToString("N0", CultureInfo.CurrentCulture)} records | User: {generatedBy} | Company: {companyCode} | At: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            Columns = columns,
            Rows = rows,
            Rtl = LangDirSv.IsRtl(App.CurrentLanguageCode),
            Translate = key => translatedHeaders.TryGetValue(key, out var label) ? label : key,
            DatasetIdentityHeader = CreateDatasetIdentityHeader(option, columns, translatedHeaders)
        };
    }

    internal static IReadOnlyList<string> GetMaterialCardExportFieldNames(
        string tableName,
        IEnumerable<string> availableFieldNames)
    {
        ArgumentNullException.ThrowIfNull(availableFieldNames);
        if (!tableName.Equals("materials", StringComparison.OrdinalIgnoreCase))
            return availableFieldNames.ToArray();

        var available = new HashSet<string>(availableFieldNames, StringComparer.OrdinalIgnoreCase);
        return MaterialCardExportFields
            .Where(available.Contains)
            .ToList();
    }


    public static async Task<ExcelImportPreview> PreviewExcelAsync(
        string path,
        DataExportOption option,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(option);
        var sheet = ReadExcelSheet(path);
        var marker = sheet.Headers.FirstOrDefault(header => header.StartsWith(DatasetIdentityPrefix, StringComparison.Ordinal));
        if (marker != null)
        {
            DatasetIdentity identity;
            try
            {
                var bytes = Convert.FromBase64String(marker[DatasetIdentityPrefix.Length..]);
                identity = JsonSerializer.Deserialize<DatasetIdentity>(XptCry.Decrypt(bytes, XptCry.KindDatasetIdentity))
                    ?? throw new InvalidDataException("The workbook dataset identity is empty.");
            }
            catch (Exception ex) when (ex is FormatException or XptCry.XptCryException or JsonException)
            {
                throw new InvalidDataException("The workbook dataset identity is invalid or belongs to another installation.", ex);
            }

            var dataRows = sheet.Rows.Count(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell)));
            return new ExcelImportPreview(
                true,
                identity.Key,
                identity.TableName,
                dataRows,
                identity.Fields,
                [],
                !string.Equals(identity.Key, option.Key, StringComparison.Ordinal));
        }

        var (plain, filtered, skipped) = await BuildPlainWorkbookIdentityAsync(sheet, option, ct);
        return new ExcelImportPreview(
            false,
            option.Key,
            option.TableName,
            filtered.Rows.Count(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell))),
            plain.Fields,
            skipped,
            false);
    }

    private static async Task<DataExportOption?> ResolveOptionByKeyAsync(string key, CancellationToken ct)
    {
        var options = await GetAvailableOptionsAsync(ct);
        return options.FirstOrDefault(option => string.Equals(option.Key, key, StringComparison.Ordinal));
    }

    public static async Task<ExcelImportResult> ImportExcelAsync(
        string path,
        DataExportOption option,
        string companyCode,
        bool replaceMode = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(option);
        // Step 10 (DX-03): service-layer DATA.IMPORT gate — before any
        // file or database work. The dataset policy (PackageOnly /
        // EngineOwned refusal) is enforced in ImportRowsAsync against
        // the FINAL dataset, which can differ from the selection when a
        // marked workbook's identity is followed.
        DataExchangeGuard.RequireImportPermission("Excel dataset import");
        try
        {
            if (option.IsSystemInfo || !Catalog.Any(x => x.Key == option.Key && x.Filter == option.Filter &&
                    x.TableCandidates.Contains(option.TableName, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The selected dataset cannot be imported.");

            var selectedKey = option.Key;
            IReadOnlyList<string> plainSkipped = [];
            var sheet = ReadExcelSheet(path);
            var markerIndexes = sheet.Headers
                .Select((header, index) => (header, index))
                .Where(x => x.header.StartsWith(DatasetIdentityPrefix, StringComparison.Ordinal))
                .ToArray();
            if (markerIndexes.Length > 1)
                throw new InvalidDataException("This workbook contains more than one ShouTech dataset identity column.");

            DatasetIdentity identity;
            var identityColumnIndex = -1;
            if (markerIndexes.Length == 1)
            {
                identityColumnIndex = markerIndexes[0].index;
                try
                {
                    var marker = Convert.FromBase64String(markerIndexes[0].header[DatasetIdentityPrefix.Length..]);
                    var payload = XptCry.Decrypt(marker, XptCry.KindDatasetIdentity);
                    identity = JsonSerializer.Deserialize<DatasetIdentity>(payload)
                        ?? throw new InvalidDataException("The workbook dataset identity is empty.");
                }
                catch (Exception ex) when (ex is FormatException or XptCry.XptCryException or JsonException)
                {
                    throw new InvalidDataException("The workbook dataset identity is invalid or belongs to another installation.", ex);
                }

                if (identity.Version != DatasetIdentityVersion)
                    throw new InvalidDataException("This workbook was produced by a newer ShouTech export. Update the application before importing it.");

                var followed = !string.Equals(identity.Key, option.Key, StringComparison.Ordinal);
                if (followed)
                {
                    var resolved = await ResolveOptionByKeyAsync(identity.Key, ct);
                    if (resolved == null)
                        throw new InvalidDataException($"The workbook is a '{identity.Key}' export, and that dataset is not available in this database.");
                    option = resolved;
                }
            }
            else
            {
                (identity, sheet, var skipped) = await BuildPlainWorkbookIdentityAsync(sheet, option, ct);
                plainSkipped = skipped;
            }

            var rowsResult = await ImportRowsAsync(
                sheet, identity, option, companyCode, identityColumnIndex, replaceMode, ct);
            var result = rowsResult with
            {
                DataRows = sheet.Rows.Count(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell))),
                MatchedColumns = identity.Fields,
                SkippedColumns = plainSkipped
                    .Concat(rowsResult.RejectedBalanceColumns.Select(c => $"{c} (balance - not imported)"))
                    .ToArray(),
                FollowedWorkbookIdentity = markerIndexes.Length == 1 &&
                    !string.Equals(identity.Key, selectedKey, StringComparison.Ordinal)
            };
            LogOp("ImportExcel",
                $"key={option.Key} | table={option.TableName} | mode={(replaceMode ? "Replace" : "Upsert")} | upserted={result.UpsertedRows} | deleted={result.DeletedRows} | company={companyCode} | file={path}");
            // Step 10 (DX-06): durable security-audit row for the import.
            await DataExchangeGuard.WriteImportAuditAsync(
                $"operation=Excel dataset import | dataset={option.Key} | table={option.TableName} | mode={(replaceMode ? "Replace" : "Upsert")} | upserted={result.UpsertedRows} | deleted={result.DeletedRows} | company={companyCode} | file={Path.GetFileName(path)} | rejectedBalanceColumns={(result.RejectedBalanceColumns.Count == 0 ? "-" : string.Join(",", result.RejectedBalanceColumns))}");
            return result;
        }
        catch (Exception ex)
        {
            LogError(
                $"ImportExcel failed | key={option.Key} | mode={(replaceMode ? "Replace" : "Upsert")} | company={companyCode} | file={path} | {ex.Message}",
                ex);
            throw;
        }
    }

    public static async Task<int> ImportEncryptedAsync(
        string path,
        DataExportOption option,
        string companyCode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(option);
        // Step 10 (DX-03): the .shx path is an import like any other —
        // same DATA.IMPORT gate, same dataset policy, same audit.
        DataExchangeGuard.RequireImportPermission("Encrypted dataset import");
        if (option.IsSystemInfo || !Catalog.Any(x => x.Key == option.Key && x.Filter == option.Filter &&
                x.TableCandidates.Contains(option.TableName, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The selected dataset cannot be imported.");

        if (!string.Equals(Path.GetExtension(path), ".shx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .shx encrypted dataset files can be imported here.");
        var payload = XptCry.DecryptFromFile(path, XptCry.KindGenericData);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("report", out var reportElement) ||
            reportElement.GetString() != $"DataExport.{option.Key}")
            throw new InvalidDataException("The encrypted file belongs to a different dataset type.");

        var sourceColumns = root.GetProperty("columns").EnumerateArray().ToArray();
        var fields = sourceColumns.Select(column => column.GetProperty("HeaderTextKey").GetString()
            ?? throw new InvalidDataException("An encrypted dataset column has no field name.")).ToArray();
        var headers = fields.ToArray();
        var types = sourceColumns.Select(column => NormalizeEncryptedType(column.GetProperty("type").GetString())).ToArray();
        var identity = new DatasetIdentity(
            DatasetIdentityVersion, option.Key, option.TableName, headers, fields, types);
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var sourceRow in root.GetProperty("rows").EnumerateArray())
        {
            var row = new string?[sourceColumns.Length];
            for (var i = 0; i < sourceColumns.Length; i++)
            {
                var value = sourceRow.GetProperty(sourceColumns[i].GetProperty("Key").GetString()!);
                row[i] = value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => throw new InvalidDataException("The encrypted dataset contains an unsupported cell value.")
                };
            }
            rows.Add(row);
        }

        var encResult = await ImportRowsAsync(new ExcelSheet(headers, rows), identity, option, companyCode, -1, replaceMode: false, ct);
        await DataExchangeGuard.WriteImportAuditAsync(
            $"operation=Encrypted dataset import | dataset={option.Key} | table={option.TableName} | mode=Upsert | upserted={encResult.UpsertedRows} | deleted=0 | company={companyCode} | file={Path.GetFileName(path)} | rejectedBalanceColumns={(encResult.RejectedBalanceColumns.Count == 0 ? "-" : string.Join(",", encResult.RejectedBalanceColumns))}");
        return encResult.UpsertedRows;
    }

    internal static Task<SteiResult> ExportDatasetPackageAsync(
        string path,
        string password,
        DataExportOption option,
        XptServ.ExportModel model,
        IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (option.IsSystemInfo || !Catalog.Any(x => x.Key == option.Key && x.Filter == option.Filter &&
                x.TableCandidates.Contains(option.TableName, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The selected dataset cannot be exported as an importable package.");

        var fields = model.Columns.Select(column => column.HeaderTextKey).ToArray();
        var rows = model.Rows.Select(row => (IReadOnlyDictionary<string, object?>)fields
            .Select((field, index) => new KeyValuePair<string, object?>(
                field,
                row.TryGetValue(model.Columns[index].Key, out var value) ? value : null))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return SteiSvc.ExportDatasetAsync(
            path, password, option.Key, option.TableName, rows, fields, progress, ct);
    }

    internal static Task<SteiResult> ImportDatasetPackageAsync(
        string path,
        string password,
        DataExportOption option,
        bool simulation = false,
        IProgress<(long done, long total)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (option.IsSystemInfo || !Catalog.Any(x => x.Key == option.Key && x.Filter == option.Filter &&
                x.TableCandidates.Contains(option.TableName, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The selected dataset cannot be imported from a package.");
        return SteiSvc.ImportDatasetAsync(
            path, password, option.Key, option.TableName, simulation, progress, ct);
    }

    public static string GetDatasetPackageFileName(DataExportOption option)
        // Step 10: every dataset gets its OWN package file name — the
        // previous family mapping collapsed distinct datasets onto one
        // name (all product-family datasets -> items.stei), inviting
        // accidental overwrites of a different dataset's package (DX-18).
        => $"{option.Key.ToLowerInvariant()}.stei";

    private static string NormalizeEncryptedType(string? type)
        => type switch
        {
            "Integer" => RptColType.WholeNumber.ToString(),
            "Decimal" => RptColType.FractionalNumber.ToString(),
            not null when Enum.TryParse<RptColType>(type, out var parsed) => parsed.ToString(),
            _ => throw new InvalidDataException("The encrypted dataset contains an unsupported column type.")
        };

    private static async Task<ExcelImportResult> ImportRowsAsync(
        ExcelSheet sheet,
        DatasetIdentity identity,
        DataExportOption option,
        string companyCode,
        int identityColumnIndex,
        bool replaceMode,
        CancellationToken ct)
    {
        // Step 10 dataset policy — the single choke point every
        // spreadsheet import flows through (Excel and .shx alike), so a
        // workbook whose identity redirects to another dataset cannot
        // bypass it. Transactional datasets exchange as .stei packages
        // (DATA-4006); engine-owned balance state changes only through
        // posted movements (DATA-4005). Replace mode inherits the same
        // rule: it is only ever offered to Master datasets.
        if (option.Policy == DatasetImportPolicy.PackageOnly)
        {
            AppHost.Errors.Report(ErrorCodes.DocumentImportRequiresPackage, null,
                $"DataExportSvc.ImportRowsAsync | dataset={option.Key} | table={option.TableName}");
            throw new AppError(
                ErrorCodes.DocumentImportRequiresPackage,
                "Documents and journal entries cannot be imported from a spreadsheet. Exchange them as an encrypted .stei dataset package.");
        }
        if (option.Policy == DatasetImportPolicy.EngineOwned)
        {
            AppHost.Errors.Report(ErrorCodes.BalanceImportNotAllowed, null,
                $"DataExportSvc.ImportRowsAsync | dataset={option.Key} | table={option.TableName}");
            throw new AppError(
                ErrorCodes.BalanceImportNotAllowed,
                "Balances and stock quantities cannot be imported from a spreadsheet. They change only through posted stock movements.");
        }
        if (replaceMode && option.Policy != DatasetImportPolicy.Master)
            throw new InvalidOperationException("Replace mode is only available for master-data datasets.");

        if (identity.Headers.Length == 0 ||
            identity.Headers.Length != identity.Fields.Length ||
            identity.Headers.Length != identity.Types.Length ||
            identity.Headers.Distinct(StringComparer.Ordinal).Count() != identity.Headers.Length)
            throw new InvalidDataException("The workbook dataset schema is invalid.");

        var visibleHeaders = sheet.Headers
            .Where((_, index) => index != identityColumnIndex)
            .ToArray();
        if (visibleHeaders.Any(string.IsNullOrWhiteSpace) ||
            visibleHeaders.Distinct(StringComparer.OrdinalIgnoreCase).Count() != visibleHeaders.Length)
            throw new InvalidDataException("The workbook contains empty or duplicate column headers.");

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var tableNames = await GetTableNamesAsync(connection, ct);
        if (!tableNames.Contains(option.TableName))
            throw new InvalidOperationException("The dataset table is not available in the current database.");

        var columns = await GetColumnNamesAsync(connection, option.TableName, ct);
        var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < identity.Headers.Length; i++)
        {
            if (!columns.Contains(identity.Fields[i], StringComparer.OrdinalIgnoreCase) ||
                !fieldNames.Add(identity.Fields[i]))
                throw new InvalidDataException("The workbook fields do not match the selected dataset.");
        }

        var keyColumns = await GetPrimaryKeyColumnsAsync(connection, option.TableName, ct);
        if (keyColumns.Count == 0 || keyColumns.Any(key => !identity.Fields.Contains(key, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("The workbook does not contain the complete primary key required for a safe import.");

        var notNullColumns = await GetNotNullColumnNamesAsync(connection, option.TableName, ct);
        var isMaterialsImport = IsMaterialsTable(option.TableName);
        var importCategoryCards = isMaterialsImport ? LoadCategoryCards() : new List<MatRepo.MatGroupRec>();
        var companyId = await FindCompanyIdAsync(connection, companyCode, ct);
        var companyColumn = columns.FirstOrDefault(x =>
            x.Equals("company_id", StringComparison.OrdinalIgnoreCase) ||
            x.Equals("companyid", StringComparison.OrdinalIgnoreCase));
        // Legacy tenant tables store the company CODE in company_id; newer tables
        // store the internal companies.company_id. Keep this mapping explicit so an
        // import cannot silently write an internal numeric ID into a text code column.
        var companyScopeValue = IsCompanyCodeScopedTable(option.TableName)
            ? companyCode.Trim()
            : companyId;
        if (companyColumn != null && companyScopeValue == null)
            throw new InvalidOperationException("The active company could not be matched to the current database.");
        if (companyColumn == null && !IsCompanyIndependent(option.TableName))
            throw new InvalidOperationException("This dataset cannot be safely restricted to the active company, so it was not imported.");

        // The hidden dataset identity remains authoritative for the target
        // table, field set and value types, but visible Excel labels are editable.
        // Re-resolve each edited label against that trusted field set so users
        // can rename translated/business-facing headers without breaking import.
        var fieldTypes = identity.Fields
            .Select((field, i) => new { field, type = identity.Types[i] })
            .ToDictionary(x => x.field, x => x.type, StringComparer.OrdinalIgnoreCase);
        var headerToField = new Dictionary<string, (string field, string type)>(StringComparer.Ordinal);
        var matchedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in visibleHeaders)
        {
            // Keep the encrypted identity authoritative for known exported
            // headers, while allowing users to remove columns or append their
            // own notes/calculated columns in Excel. Unknown columns are
            // ignored; duplicate mappings remain an error to prevent one
            // visible value from ambiguously overwriting a database field.
            var field = ResolveImportField(
                header, identity.Headers, identity.Fields, option.TableName);
            if (field == null)
                continue;
            if (!fieldTypes.TryGetValue(field, out var type) || !matchedFields.Add(field))
                throw new InvalidDataException($"Column '{header}' maps more than once or does not belong to the exported dataset.");
            headerToField.Add(header, (field, type));
        }
        if (headerToField.Count == 0)
            throw new InvalidDataException("No editable columns in this workbook match the exported dataset.");

        var columnIndexes = sheet.Headers
            .Select((header, index) => (header, index))
            .Where(x => x.index != identityColumnIndex)
            .ToDictionary(x => x.header, x => x.index, StringComparer.Ordinal);
        var balanceColumns = BalanceColumnsByTable.TryGetValue(option.TableName, out var tableBalanceColumns)
            ? tableBalanceColumns
            : [];
        var rejectedBalanceColumns = new List<string>();
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            var processed = 0;
            var importedKeys = new List<string>();
            for (var rowIndex = 0; rowIndex < sheet.Rows.Count; rowIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var sourceRow = sheet.Rows[rowIndex];
                if (identityColumnIndex >= 0 && identityColumnIndex < sourceRow.Count &&
                    !string.IsNullOrWhiteSpace(sourceRow[identityColumnIndex]))
                    throw new InvalidDataException("The hidden dataset identity column must not contain row data.");
                if (sourceRow.All(string.IsNullOrWhiteSpace))
                    continue;

                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var mapping in headerToField)
                {
                    var cellIndex = columnIndexes[mapping.Key];
                    var cell = cellIndex < sourceRow.Count ? sourceRow[cellIndex] : null;
                    object? parsed;
                    try
                    {
                        parsed = ParseExcelValue(cell, mapping.Value.type);
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new InvalidDataException(
                            $"Column '{mapping.Value.field}' contains value '{cell}' but expects {mapping.Value.type}: {ex.Message}", ex);
                    }
                    values[mapping.Value.field] =
                        mapping.Value.field.Equals("name", StringComparison.OrdinalIgnoreCase)
                            ? parsed ?? string.Empty
                            : parsed;
                }

                // Material cards use one user-entered name in materials.name.
                if (option.TableName.Equals("materials", StringComparison.OrdinalIgnoreCase))
                {
                    if (!values.TryGetValue("name", out var canonicalName) ||
                        string.IsNullOrWhiteSpace(Convert.ToString(canonicalName, CultureInfo.InvariantCulture)))
                        throw new InvalidDataException("Product rows require a non-empty 'name' column.");
                }

                // Product cards: the sheet shows the category NAME; store the card code.
                if (isMaterialsImport && values.TryGetValue("category", out var categoryCell) && categoryCell is string categoryText)
                    values["category"] = CategoryNameToCode(categoryText, importCategoryCards)!;

                // Blank cell + NOT NULL column: omit it (DEFAULT applies on insert,
                // existing value kept on update) instead of writing NULL.
                foreach (var column in notNullColumns)
                {
                    if (values.TryGetValue(column, out var cellValue) && cellValue is null &&
                        !keyColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                        values.Remove(column);
                }

                if (companyColumn != null)
                    values[companyColumn] = companyScopeValue!;

                // Party datasets are split by kind: a Customers import
                // can only ever write customer rows (Step 10 — before,
                // Customers and Suppliers were the same unfiltered
                // parties table on both the export and import sides).
                var forcedKind = PartyKindFor(option.Filter);
                if (forcedKind != null && columns.Contains("kind", StringComparer.OrdinalIgnoreCase))
                    values["kind"] = forcedKind;

                // Step 10 (DX-04): live balance columns are stripped —
                // reported, never written. The rest of the row (the
                // master data) still imports.
                foreach (var balanceColumn in balanceColumns)
                {
                    if (values.Remove(balanceColumn) && !rejectedBalanceColumns.Contains(balanceColumn))
                        rejectedBalanceColumns.Add(balanceColumn);
                }

                try
                {
                    ValidateDatasetRow(option, values);
                    processed += await UpsertExcelRowAsync(
                        connection, transaction, option.TableName, values, columns, keyColumns, ct);
                    if (replaceMode)
                        importedKeys.Add(BuildCompositeKey(values, keyColumns));
                }
                catch (Exception ex)
                {
                    LogError($"Import row failed | key={option.Key} | table={option.TableName} | sheetRow={rowIndex + 2} | {ex.Message}", ex);
                    throw new InvalidDataException($"Import stopped at spreadsheet row {rowIndex + 2}: {ex.Message}", ex);
                }
            }

            var deleted = 0;
            if (replaceMode)
            {
                // Database rows for this company that are absent from the file are removed.
                // Product/material tables prefer soft-delete (is_deleted) when the column exists.
                deleted = await DeleteRowsNotInImportAsync(
                    connection, transaction, option.TableName, keyColumns,
                    companyColumn, companyScopeValue, importedKeys, ct);
                LogInfo($"Replace cleanup | key={option.Key} | table={option.TableName} | upserted={processed} | removed={deleted} | company={companyCode}");
            }

            await transaction.CommitAsync(ct);
            return new ExcelImportResult(processed, deleted, replaceMode, option.Key, option.TableName)
            {
                RejectedBalanceColumns = rejectedBalanceColumns
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static string BuildCompositeKey(Dictionary<string, object?> values, List<string> keyColumns)
    {
        var parts = new string[keyColumns.Count];
        for (var i = 0; i < keyColumns.Count; i++)
        {
            values.TryGetValue(keyColumns[i], out var value);
            parts[i] = value?.ToString() ?? string.Empty;
        }
        return string.Join("\u001f", parts);
    }

    private static async Task<int> DeleteRowsNotInImportAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        List<string> keyColumns,
        string? companyColumn,
        object? companyId,
        IReadOnlyList<string> keepKeys,
        CancellationToken ct)
    {
        if (keyColumns.Count == 0)
            return 0;

        // Replace cleanup must never run without an explicit company scope.
        // This also blocks accidental cross-company cleanup for shared/global tables.
        if (string.IsNullOrWhiteSpace(companyColumn) || companyId is null)
            throw new InvalidOperationException(
                $"Replace mode cannot safely remove missing rows from '{table}' without a verified company scope. Use upsert mode instead.");

        var columns = await GetColumnNamesAsync(connection, table, ct);
        var softCol = columns.FirstOrDefault(c => c.Equals("is_deleted", StringComparison.OrdinalIgnoreCase));
        if (softCol is null)
            throw new InvalidOperationException(
                $"Replace mode cannot remove missing rows from '{table}' because the table does not support soft deletion. Use upsert mode instead.");

        var deletedAtCol = columns.FirstOrDefault(c => c.Equals("deleted_at", StringComparison.OrdinalIgnoreCase));
        var keep = new HashSet<string>(keepKeys, StringComparer.Ordinal);

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        var keySelect = string.Join(", ", keyColumns.Select(QuoteIdentifier));
        var where = new List<string>
        {
            $"{QuoteIdentifier(companyColumn!)} = @companyId",
            $"{QuoteIdentifier(softCol)} = {(DatabaseRuntime.IsPostgreSql() ? "FALSE" : "0")}"
        };
        var companyParameter = select.CreateParameter();
        companyParameter.ParameterName = "@companyId";
        companyParameter.Value = companyId;
        select.Parameters.Add(companyParameter);

        select.CommandText =
            $"SELECT {keySelect} FROM {QuoteIdentifier(table)}" +
            (where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where));

        var toRemove = new List<object?[]>();
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var parts = new string[keyColumns.Count];
                var raw = new object?[keyColumns.Count];
                for (var i = 0; i < keyColumns.Count; i++)
                {
                    raw[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    parts[i] = raw[i]?.ToString() ?? string.Empty;
                }
                if (!keep.Contains(string.Join("\u001f", parts)))
                    toRemove.Add(raw);
            }
        }

        var deleted = 0;
        foreach (var rawKeys in toRemove)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            var predicates = new List<string>();
            for (var i = 0; i < keyColumns.Count; i++)
            {
                var name = $"@k{i}";
                predicates.Add($"{QuoteIdentifier(keyColumns[i])} = {name}");
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = rawKeys[i] ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
            predicates.Add($"{QuoteIdentifier(companyColumn!)} = @companyId");
            var removeCompanyParameter = cmd.CreateParameter();
            removeCompanyParameter.ParameterName = "@companyId";
            removeCompanyParameter.Value = companyId;
            cmd.Parameters.Add(removeCompanyParameter);

            {
                var deletedByCol = columns.FirstOrDefault(c => c.Equals("deleted_by", StringComparison.OrdinalIgnoreCase));
                var sets = new List<string>
                {
                    $"{QuoteIdentifier(softCol)} = {(DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1")}"
                };
                if (deletedAtCol != null)
                    sets.Add($"{QuoteIdentifier(deletedAtCol)} = @deletedAt");
                if (deletedByCol != null)
                    sets.Add($"{QuoteIdentifier(deletedByCol)} = @deletedBy");
                cmd.CommandText =
                    $"UPDATE {QuoteIdentifier(table)} SET {string.Join(", ", sets)} WHERE {string.Join(" AND ", predicates)}";
                if (deletedAtCol != null)
                {
                    var pAt = cmd.CreateParameter();
                    pAt.ParameterName = "@deletedAt";
                    pAt.Value = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    cmd.Parameters.Add(pAt);
                }
                if (deletedByCol != null)
                {
                    var pBy = cmd.CreateParameter();
                    pBy.ParameterName = "@deletedBy";
                    pBy.Value = AppState.CurrentUser?.Username ?? AppState.CurrentUser?.FullName ?? "system";
                    cmd.Parameters.Add(pBy);
                }
            }

            deleted += await cmd.ExecuteNonQueryAsync(ct);
        }

        return deleted;
    }

    private static void ValidateDatasetRow(

        DataExportOption option,
        IReadOnlyDictionary<string, object?> row)
    {
        if ((option.Filter is DataExportFilter.Sales or DataExportFilter.Returns) &&
            (option.TableName.Equals("pos_transactions", StringComparison.OrdinalIgnoreCase) ||
             option.TableName.Equals("postransactions", StringComparison.OrdinalIgnoreCase)))
        {
            var type = GetRowValue(row, "transaction_type")?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(type) && IsReturnType(type) != (option.Filter == DataExportFilter.Returns))
                throw new InvalidDataException("This row's transaction type does not match the selected sales/returns dataset.");
        }

        if (option.Filter == DataExportFilter.OpeningStock &&
            option.TableName.Contains("stock", StringComparison.OrdinalIgnoreCase))
        {
            var type = GetRowValue(row, "movement_type")?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(type) && !IsOpeningStockType(type))
                throw new InvalidDataException("This movement is not an opening-stock record.");
        }

        // Step 10: a row must belong to the dataset's own slice — a
        // sales-returns workbook cannot carry a sales-invoice row, and a
        // Customers workbook cannot carry a supplier row.
        var docKinds = DocKindsFor(option.Filter);
        if (docKinds.Length > 0)
        {
            var docKind = GetRowValue(row, "doc_kind")?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(docKind) &&
                !docKinds.Contains(docKind.Trim(), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("This row's document kind does not match the selected dataset.");
        }

        var partyKind = PartyKindFor(option.Filter);
        if (partyKind != null)
        {
            var kind = GetRowValue(row, "kind")?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(kind) &&
                !kind.Trim().Equals(partyKind, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This row's party kind does not match the selected dataset.");
        }
    }

    private static object? GetRowValue(IReadOnlyDictionary<string, object?> row, string column)
        => row.FirstOrDefault(x => x.Key.Equals(column, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool IsReturnType(string value)
    {
        var normalized = value.ToLowerInvariant();
        return normalized.Contains("return", StringComparison.Ordinal) ||
            normalized.Contains("refund", StringComparison.Ordinal);
    }

    private static bool IsOpeningStockType(string value)
        => value.Contains("open", StringComparison.OrdinalIgnoreCase);

    private const string DatasetIdentityPrefix = "__SHOUTECH_DATASET_V1__:";
    private const int DatasetIdentityVersion = 1;

    private static string CreateDatasetIdentityHeader(
        DataExportOption option,
        IReadOnlyList<RptCol> columns,
        Dictionary<string, string> translatedHeaders)
    {
        var identity = new DatasetIdentity(
            DatasetIdentityVersion,
            option.Key,
            option.TableName,
            columns.Select(c => translatedHeaders.TryGetValue(c.HeaderTextKey, out var label) ? label : c.HeaderTextKey).ToArray(),
            columns.Select(c => c.HeaderTextKey).ToArray(),
            columns.Select(c => c.Type.ToString()).ToArray());
        var encrypted = XptCry.Encrypt(
            JsonSerializer.SerializeToUtf8Bytes(identity),
            XptCry.KindDatasetIdentity);
        return DatasetIdentityPrefix + Convert.ToBase64String(encrypted);
    }

    private static XptServ.ExportModel BuildSystemInfoModel(
        DataExportOption option,
        string companyCode,
        string companyName,
        string generatedBy)
    {
        var connectionString = DatabaseRuntime.GetConnectionString();
        var databaseName = DatabaseRuntime.IsSqlite()
            ? Path.GetFileName(DatabaseRuntime.GetSqliteDataSource(connectionString ?? string.Empty) ?? "-")
            : DatabaseRuntime.IsPostgreSql()
                ? new NpgsqlConnectionStringBuilder(connectionString).Database ?? "-"
                : "-";
        var safeCompanyCode = new string(companyCode
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
            .ToArray()).Trim('.');
        var backupDirectory = AppPaths.CompanyBackup(
            string.IsNullOrWhiteSpace(safeCompanyCode) ? "default" : safeCompanyCode);
        var backupCount = Directory.Exists(backupDirectory)
            ? Directory.EnumerateFiles(backupDirectory, "*", SearchOption.TopDirectoryOnly).Count()
            : 0;
        var values = new object?[]
        {
            companyCode,
            companyName,
            databaseName,
            DatabaseRuntime.GetProvider(),
            backupCount,
            Environment.UserName,
            DateTime.Now
        };
        var fields = new[] { "CompanyCode", "CompanyName", "DatabaseName", "Provider", "BackupCount", "User", "GeneratedAt" };
        var columns = fields.Select((field, index) => new RptCol
        {
            Key = $"Column{index + 1}",
            HeaderTextKey = field,
            Type = index == 4 ? RptColType.WholeNumber : index == 6 ? RptColType.DateTime : RptColType.Text,
            Width = 150
        }).ToArray();
        var row = columns.Select((column, index) => (column.Key, values[index]))
            .ToDictionary(x => x.Key, x => x.Item2, StringComparer.Ordinal);
        var labels = fields.ToDictionary(
            field => field,
            field => AppServiceHost.Localization.GetString(
                $"DataExchange.Columns.{field}", Humanize(field)),
            StringComparer.Ordinal);

        return new XptServ.ExportModel
        {
            ReportKey = "DataExport.SystemInfo",
            Title = option.DisplayName,
            CompanyName = companyName,
            GeneratedBy = generatedBy,
            FilterSummary = "1 record",
            Columns = columns,
            Rows = [row],
            Rtl = LangDirSv.IsRtl(App.CurrentLanguageCode),
            Translate = key => labels.TryGetValue(key, out var label) ? label : key
        };
    }

    private static ExcelSheet ReadExcelSheet(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .xlsx workbooks can be imported as editable datasets.");

        using var archive = ZipFile.OpenRead(path);
        var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidDataException("The workbook does not contain a first worksheet.");
        var sharedStrings = ReadSharedStrings(archive);
        using var stream = sheetEntry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = document.Root?.Element(ns + "sheetData")?.Elements(ns + "row").ToArray()
            ?? throw new InvalidDataException("The workbook worksheet is empty.");
        var matrix = new List<IReadOnlyList<string?>>();
        foreach (var row in rows)
        {
            var cells = new Dictionary<int, string?>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                var reference = (string?)cell.Attribute("r")
                    ?? throw new InvalidDataException("A workbook cell has no column reference.");
                var index = ExcelColumnIndex(reference);
                var type = (string?)cell.Attribute("t");
                var value = type switch
                {
                    "inlineStr" => string.Concat(cell.Element(ns + "is")?.Descendants(ns + "t").Select(x => x.Value) ?? []),
                    "s" => SharedStringValue(cell.Element(ns + "v")?.Value, sharedStrings),
                    "b" => cell.Element(ns + "v")?.Value == "1" ? "true" : "false",
                    _ => cell.Element(ns + "v")?.Value
                };
                cells[index] = value;
            }

            var width = cells.Count == 0 ? 0 : cells.Keys.Max() + 1;
            var values = new string?[width];
            foreach (var pair in cells)
                values[pair.Key] = pair.Value;
            matrix.Add(values);
        }

        if (matrix.Count == 0 || matrix[0].Count == 0)
            throw new InvalidDataException("The workbook header row is missing.");

        var header = matrix[0].Select(x => x?.Trim() ?? string.Empty).ToArray();
        var last = header.Length - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(header[last]))
            last--;
        if (last < 0)
            throw new InvalidDataException("The workbook header row is missing.");
        header = header.Take(last + 1).ToArray();
        if (header.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("The workbook header row contains an empty column name.");

        var body = matrix.Skip(1).Select(row =>
        {
            var values = new string?[header.Length];
            for (var i = 0; i < header.Length && i < row.Count; i++)
                values[i] = row[i];
            return (IReadOnlyList<string?>)values;
        }).ToArray();
        return new ExcelSheet(header, body);
    }

    private static string[] ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry == null)
            return Array.Empty<string>();

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Root?.Elements(ns + "si")
            .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
            .ToArray() ?? [];
    }

    private static string? SharedStringValue(string? indexText, string[] sharedStrings)
    {
        if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
            index < 0 || index >= sharedStrings.Length)
            throw new InvalidDataException("The workbook contains an invalid shared-string reference.");
        return sharedStrings[index];
    }

    private static int ExcelColumnIndex(string cellReference)
    {
        var value = 0;
        var foundColumn = false;
        foreach (var character in cellReference)
        {
            if (!char.IsLetter(character))
                break;
            foundColumn = true;
            value = checked(value * 26 + (char.ToUpperInvariant(character) - 'A' + 1));
        }
        if (!foundColumn)
            throw new InvalidDataException($"Invalid Excel cell reference: {cellReference}.");
        return value - 1;
    }

    private static object? ParseExcelValue(string? value, string type)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!Enum.TryParse<RptColType>(type, out var columnType))
            throw new InvalidDataException("The workbook contains an unsupported data type.");

        return columnType switch
        {
            RptColType.WholeNumber => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)
                ? whole
                : throw new InvalidDataException($"'{value}' is not a valid whole number."),
            RptColType.FractionalNumber or RptColType.Money => ParseDecimalValue(value),
            RptColType.Boolean => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"
                ? true
                : value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0"
                    ? false
                    : throw new InvalidDataException($"'{value}' is not a valid Boolean value."),
            RptColType.Date or RptColType.DateTime => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
                ? date
                : value,
            _ => value
        };
    }

    private static decimal ParseDecimalValue(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            throw new InvalidDataException($"'{value}' is not a valid invariant-culture number.");

        var scale = (decimal.GetBits(number)[3] >> 16) & 0x7F;
        if (scale > 6)
            throw new InvalidDataException($"'{value}' has more than 6 decimal places; values are not rounded or truncated during import.");
        return number;
    }

    /// <summary>
    /// Accepts a plain .xlsx whose first row uses database column names,
    /// exported headers, or the same names with spaces instead of underscores.
    /// Columns that do not match the selected table are dropped.
    /// </summary>
    private static async Task<(DatasetIdentity Identity, ExcelSheet Sheet, IReadOnlyList<string> Skipped)> BuildPlainWorkbookIdentityAsync(
        ExcelSheet sheet,
        DataExportOption option,
        CancellationToken ct)
        => await BuildPlainWorkbookIdentityAsync(sheet, option, allowPromoteHeader: true, ct: ct);

    private static async Task<(DatasetIdentity Identity, ExcelSheet Sheet, IReadOnlyList<string> Skipped)> BuildPlainWorkbookIdentityAsync(
        ExcelSheet sheet,
        DataExportOption option,
        bool allowPromoteHeader,
        CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var columns = await GetColumnNamesAsync(connection, option.TableName, ct);
        var types = await GetColumnTypeMapAsync(connection, option.TableName, ct);
        if (columns.Count == 0)
            throw new InvalidOperationException("The selected dataset table has no columns.");

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keepIndexes = new List<int>();
        var headers = new List<string>();
        var fields = new List<string>();
        var typeNames = new List<string>();
        for (var i = 0; i < sheet.Headers.Count; i++)
        {
            var header = sheet.Headers[i];
            var match = MatchImportColumn(header, columns, option.TableName);
            if (match == null || !used.Add(match))
                continue;
            keepIndexes.Add(i);
            headers.Add(header);
            fields.Add(match);
            typeNames.Add(types.TryGetValue(match, out var columnType)
                ? columnType
                : RptColType.Text.ToString());
        }

        if (fields.Count == 0 && allowPromoteHeader && sheet.Rows.Count > 0)
        {
            var promoted = sheet.Rows[0].Select(cell => cell?.Trim() ?? string.Empty).ToList();
            while (promoted.Count > 0 && string.IsNullOrWhiteSpace(promoted[^1]))
                promoted.RemoveAt(promoted.Count - 1);
            if (promoted.Count > 0 && promoted.All(name => !string.IsNullOrWhiteSpace(name)))
            {
                var rest = sheet.Rows.Skip(1).ToArray();
                return await BuildPlainWorkbookIdentityAsync(
                    new ExcelSheet(promoted, rest), option, allowPromoteHeader: false, ct: ct);
            }
        }

        if (fields.Count == 0)
            throw new InvalidDataException(
                "No columns in this Excel file match the selected dataset. Put the database column names (or a ShouTech export header row) in the first row, then import again.");

        var rows = new List<IReadOnlyList<string?>>(sheet.Rows.Count);
        foreach (var source in sheet.Rows)
        {
            var values = new string?[keepIndexes.Count];
            for (var i = 0; i < keepIndexes.Count; i++)
            {
                var index = keepIndexes[i];
                values[i] = index < source.Count ? source[index] : null;
            }
            rows.Add(values);
        }

        var identity = new DatasetIdentity(
            DatasetIdentityVersion,
            option.Key,
            option.TableName,
            headers.ToArray(),
            fields.ToArray(),
            typeNames.ToArray());
        var skipped = new List<string>();
        for (var i = 0; i < sheet.Headers.Count; i++)
        {
            if (!keepIndexes.Contains(i) && !string.IsNullOrWhiteSpace(sheet.Headers[i]))
                skipped.Add(sheet.Headers[i]);
        }
        return (identity, new ExcelSheet(headers, rows), skipped);
    }

    internal static string? ResolveImportField(
        string header,
        IReadOnlyList<string> exportedHeaders,
        IReadOnlyList<string> fields,
        string tableName)
    {
        if (exportedHeaders.Count != fields.Count)
            throw new InvalidDataException("The workbook dataset schema is invalid.");

        var identityHeaderIndex = -1;
        for (var i = 0; i < exportedHeaders.Count; i++)
        {
            if (string.Equals(exportedHeaders[i], header, StringComparison.OrdinalIgnoreCase))
            {
                identityHeaderIndex = i;
                break;
            }
        }

        return identityHeaderIndex >= 0
            ? fields[identityHeaderIndex]
            : MatchImportColumn(header, fields, tableName);
    }

    private static string? MatchImportColumn(
        string header,
        IReadOnlyList<string> columns,
        string tableName)
    {
        if (string.IsNullOrWhiteSpace(header))
            return null;

        static string Compact(string value)
            => new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        // Material-card exports intentionally use business-facing labels
        // ("Internal ID", "Item No") instead of the physical database names.
        // The encrypted workbook marker normally preserves this mapping, but
        // Excel editors and third-party spreadsheet tools may remove that
        // hidden marker when a user edits/saves the workbook. Keep a stable
        // fallback mapping so the exported workbook remains editable/importable.
        if (tableName.Equals("materials", StringComparison.OrdinalIgnoreCase))
        {
            var alias = Compact(header);
            var target = alias switch
            {
                "internalid" or "materialid" or "id" => "material_id",
                "itemno" or "itemnumber" or "itemcode" or "itemnumbercode" => "code",
                _ => null
            };
            if (target != null)
            {
                var actual = columns.FirstOrDefault(column =>
                    column.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (actual != null)
                    return actual;
            }
        }

        var compactHeader = Compact(header);
        var snakeHeader = Compact(ToSnakeCase(header.Replace(' ', '_')));
        foreach (var column in columns)
        {
            if (string.Equals(column, header, StringComparison.OrdinalIgnoreCase))
                return column;
            var compactColumn = Compact(column);
            if (compactColumn.Length > 0 &&
                (compactColumn == compactHeader || compactColumn == snakeHeader ||
                 compactColumn == Compact(Humanize(column)) && compactColumn == compactHeader))
                return column;
            if (string.Equals(Humanize(column), header.Trim(), StringComparison.OrdinalIgnoreCase))
                return column;
        }
        return null;
    }

    private static async Task<Dictionary<string, string>> GetColumnTypeMapAsync(
        DbConnection connection,
        string table,
        CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (DatabaseRuntime.IsSqlite())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)})";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                map[reader.GetString(1)] = MapStorageType(reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        else
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT column_name, data_type
                FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = @table
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@table";
            parameter.Value = table;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                map[reader.GetString(0)] = MapStorageType(reader.GetString(1));
        }

        // SQLite stores Boolean flags as INTEGER. Apply the same
        // language-neutral column conventions to both providers.
        foreach (var column in map.Keys.ToArray())
        {
            if (IsBooleanColumnName(column))
                map[column] = RptColType.Boolean.ToString();
        }

        return map;
    }

    private static bool IsBooleanColumnName(string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        return columnName.StartsWith("is_", StringComparison.OrdinalIgnoreCase) ||
            columnName.StartsWith("has_", StringComparison.OrdinalIgnoreCase) ||
            columnName.StartsWith("allow_", StringComparison.OrdinalIgnoreCase) ||
            columnName.StartsWith("can_", StringComparison.OrdinalIgnoreCase) ||
            columnName.StartsWith("use_", StringComparison.OrdinalIgnoreCase) ||
            columnName.StartsWith("requires_", StringComparison.OrdinalIgnoreCase) ||
            columnName.Equals("price_includes_tax", StringComparison.OrdinalIgnoreCase);
    }

    private static string MapStorageType(string? storageType)
    {
        var value = (storageType ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains("bool", StringComparison.Ordinal))
            return RptColType.Boolean.ToString();
        if (value.Contains("timestamp", StringComparison.Ordinal) ||
            value.Contains("datetime", StringComparison.Ordinal))
            return RptColType.DateTime.ToString();
        if (value == "date")
            return RptColType.Date.ToString();
        // Check decimal declarations before integer-like tokens. This keeps
        // NUMERIC(p,s), DECIMAL(p,s), REAL and floating-point columns fractional
        // even when the current stored value has no fractional component.
        if (value.Contains("real", StringComparison.Ordinal) ||
            value.Contains("double", StringComparison.Ordinal) ||
            value.Contains("float", StringComparison.Ordinal) ||
            value.Contains("numeric", StringComparison.Ordinal) ||
            value.Contains("decimal", StringComparison.Ordinal) ||
            value.Contains("money", StringComparison.Ordinal))
            return RptColType.FractionalNumber.ToString();
        if (value.Contains("int", StringComparison.Ordinal) ||
            value is "serial" or "bigserial" or "smallserial" or "smallint" or "integer" or "bigint")
            return RptColType.WholeNumber.ToString();
        return RptColType.Text.ToString();
    }

    private static async Task<List<string>> GetColumnNamesAsync(DbConnection connection, string table, CancellationToken ct)
    {
        var columns = new List<string>();
        if (DatabaseRuntime.IsSqlite())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)})";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(reader.GetString(1));
            return columns;
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_schema=current_schema() AND table_name=@table ORDER BY ordinal_position";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@table";
            parameter.Value = table;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(reader.GetString(0));
        }
        return columns;
    }

    /// <summary>
    /// Columns declared NOT NULL. A blank spreadsheet cell must never be sent
    /// as an explicit NULL to these (it bypasses the column DEFAULT and the
    /// whole import rolls back); the import omits them so the default applies.
    /// </summary>
    private static async Task<HashSet<string>> GetNotNullColumnNamesAsync(DbConnection connection, string table, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        if (DatabaseRuntime.IsSqlite())
        {
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)})";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (reader.GetInt32(3) != 0)
                    result.Add(reader.GetString(1));
        }
        else
        {
            command.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_schema=current_schema() AND table_name=@table AND is_nullable='NO'";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@table";
            parameter.Value = table;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(reader.GetString(0));
        }
        return result;
    }

    private static bool IsMaterialsTable(string table)
        => table.Equals("materials", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Category cards (material_groups) of the active company: code -> display name.
    /// materials.category stores the CARD CODE; spreadsheets show the NAME.
    /// </summary>
    // materials.category stores product_categories.category_id, not a
    // material_groups code. Resolve the actual class card so Excel displays
    // its name while retaining the stable category ID in the hidden dataset
    // identity for safe round-trip imports.
    private static List<MatRepo.ProductCategoryRec> LoadCategoryCards()
    {
        try { return MatRepo.LoadProductCategories() ?? new(); }
        catch (Exception ex)
        {
            LogWarn($"Product categories could not be loaded for the workbook: {ex.Message}");
            return new();
        }
    }

    private static string CategoryDisplayName(MatRepo.ProductCategoryRec card)
    {
        return string.IsNullOrWhiteSpace(card.Name) ? card.Code : card.Name.Trim();
    }

    /// <summary>Export: category ID or code -> category display name.</summary>
    private static string? CategoryCodeToName(string? value, IReadOnlyList<MatRepo.ProductCategoryRec> cards)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var key = value.Trim();
        var card = cards.FirstOrDefault(c =>
            string.Equals(c.Id, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Code, key, StringComparison.OrdinalIgnoreCase));
        return card == null ? value : CategoryDisplayName(card);
    }

    /// <summary>
    /// Import: a category name, code or ID from Excel -> category_id stored
    /// in materials.category. Ambiguous or unknown names remain unchanged.
    /// </summary>
    private static string? CategoryNameToCode(string? value, IReadOnlyList<MatRepo.ProductCategoryRec> cards)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var text = value.Trim();
        var byIdOrCode = cards.FirstOrDefault(c =>
            string.Equals(c.Id, text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Code, text, StringComparison.OrdinalIgnoreCase));
        if (byIdOrCode != null) return byIdOrCode.Id;
        var byName = cards.Where(c =>
                string.Equals(c.Name?.Trim(), text, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return byName.Count == 1 ? byName[0] : value;
    }

    private static async Task<List<string>> GetPrimaryKeyColumnsAsync(DbConnection connection, string table, CancellationToken ct)
    {
        var keys = new List<string>();
        await using var command = connection.CreateCommand();
        if (DatabaseRuntime.IsSqlite())
        {
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)})";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (reader.GetInt32(5) > 0)
                    keys.Add(reader.GetString(1));
        }
        else
        {
            command.CommandText = """
                SELECT kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON tc.constraint_name = kcu.constraint_name
                 AND tc.table_schema = kcu.table_schema
                 AND tc.table_name = kcu.table_name
                WHERE tc.constraint_type = 'PRIMARY KEY'
                  AND tc.table_schema = current_schema()
                  AND tc.table_name = @table
                ORDER BY kcu.ordinal_position
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@table";
            parameter.Value = table;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                keys.Add(reader.GetString(0));
        }
        return keys;
    }

    private static async Task<int> UpsertExcelRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        Dictionary<string, object?> row,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> primaryKeys,
        CancellationToken ct)
    {
        var selected = columns.Where(row.ContainsKey).ToArray();
        selected = selected.Where(column =>
            !primaryKeys.Contains(column, StringComparer.OrdinalIgnoreCase) || row[column] != null).ToArray();
        var nonKeys = selected.Where(column => !primaryKeys.Contains(column, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0)
            throw new InvalidDataException("A row contains no importable fields.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var placeholders = selected.Select((_, index) => $"@p{index}").ToArray();
        const string prefix = "INSERT";
        var suffix = nonKeys.Length == 0
            ? " ON CONFLICT DO NOTHING"
            : DatabaseRuntime.IsPostgreSql()
                ? " ON CONFLICT DO UPDATE SET " + string.Join(",", nonKeys.Select(column =>
                    $"{QuoteIdentifier(column)}=EXCLUDED.{QuoteIdentifier(column)}"))
                : " ON CONFLICT DO UPDATE SET " + string.Join(",", nonKeys.Select(column =>
                    $"{QuoteIdentifier(column)}=excluded.{QuoteIdentifier(column)}"));
        command.CommandText = $"{prefix} INTO {QuoteIdentifier(table)} ({string.Join(",", selected.Select(QuoteIdentifier))}) VALUES ({string.Join(",", placeholders)}){suffix}";
        for (var i = 0; i < selected.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = placeholders[i];
            parameter.Value = row[selected[i]] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static DbConnection CreateConnection()
    {
        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The active database connection is not configured.");

        if (DatabaseRuntime.IsSqlite())
            return new SqliteConnection(connectionString);
        if (DatabaseRuntime.IsPostgreSql())
            return new NpgsqlConnection(connectionString);
        throw new NotSupportedException("The configured database provider cannot export tabular data.");
    }

    private static async Task<HashSet<string>> GetTableNamesAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = DatabaseRuntime.IsSqlite()
            ? "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"
            : "SELECT table_name FROM information_schema.tables WHERE table_schema=current_schema() AND table_type='BASE TABLE'";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));
        return names;
    }

    private static bool IsCompanyCodeScopedTable(string tableName)
    {
        // Step 10: the company-key shape is decided by GuidCompanyTables
        // (the FK-declaring tables store the internal Guid; every other
        // catalog table stores the company CODE). The previous list put
        // chart_of_accounts / brands / product_categories /
        // units_of_measure in the CODE family, so an Accounts import
        // wrote a company code into a Guid-keyed FK column.
        return !GuidCompanyTables.Contains(tableName);
    }

    private static async Task<object?> FindCompanyIdAsync(DbConnection connection, string companyCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
            return null;

        var tables = await GetTableNamesAsync(connection, ct);
        var companyTable = tables.FirstOrDefault(x => string.Equals(x, "companies", StringComparison.OrdinalIgnoreCase));

        // Some development/tenant databases are already scoped to one company and
        // intentionally do not carry the global companies registry. In that model,
        // company_id values in tenant tables are the active company code itself.
        if (companyTable == null)
            return companyCode.Trim();

        var companyColumns = await GetColumnNamesAsync(connection, companyTable, ct);
        var idColumn = companyColumns.FirstOrDefault(x => x.Equals("company_id", StringComparison.OrdinalIgnoreCase));
        var codeColumn = companyColumns.FirstOrDefault(x => x.Equals("company_code", StringComparison.OrdinalIgnoreCase));
        if (idColumn == null || codeColumn == null)
            throw new InvalidOperationException(
                $"The '{companyTable}' table exists in the current database but does not contain the required company_id and company_code columns. Check the database schema or apply the pending migrations.");

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {QuoteIdentifier(idColumn)} FROM {QuoteIdentifier(companyTable)} " +
            $"WHERE TRIM({QuoteIdentifier(codeColumn)}) = @companyCode OR TRIM({QuoteIdentifier(idColumn)}) = @companyCode LIMIT 1";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@companyCode";
        parameter.Value = companyCode.Trim();
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync(ct);
        if (value is null or DBNull)
            throw new InvalidOperationException(
                $"Company '{companyCode}' is not registered in the current database. Verify that the selected company and database belong together, or run the company/database initialization step.");
        return value;
    }

    /// <summary>Document kinds a document/line dataset contains (Step 10).</summary>
    private static string[] DocKindsFor(DataExportFilter filter) => filter switch
    {
        DataExportFilter.SalesDocuments or DataExportFilter.SalesLines => ["SAL", "POS"],
        DataExportFilter.SalesReturnDocuments or DataExportFilter.SalesReturnLines => ["SRT"],
        DataExportFilter.PurchaseDocuments or DataExportFilter.PurchaseLines => ["PUR"],
        DataExportFilter.PurchaseReturnDocuments or DataExportFilter.PurchaseReturnLines => ["PRT"],
        _ => []
    };

    /// <summary>Party kind ('C'/'S') a party/group dataset contains, if any.</summary>
    private static string? PartyKindFor(DataExportFilter filter) => filter switch
    {
        DataExportFilter.CustomerParties or DataExportFilter.CustomerGroups => "C",
        DataExportFilter.SupplierParties or DataExportFilter.SupplierGroups => "S",
        _ => null
    };

    /// <summary>
    /// Builds the company-scoped export query for a catalog dataset
    /// (Step 10, DX-01). The predicate matches the company's OWN keys —
    /// its code and its internal Guid — because the schema stores
    /// company_id in both shapes (Steps 4/9); it can therefore never
    /// match another company's rows, whichever shape a table uses.
    /// Line tables without a company_id are scoped through their parent
    /// document. A dataset that cannot be scoped is refused, never
    /// exported unfiltered.
    /// </summary>
    private static async Task<(string Sql, List<(string Name, object Value)> Parameters)> BuildScopedExportQueryAsync(
        DbConnection connection,
        DataExportOption option,
        IReadOnlyList<string> columns,
        string companyCode,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
            throw new InvalidOperationException("The active company could not be determined, so the dataset was not exported.");

        var parameters = new List<(string Name, object Value)>();
        var keyValues = new List<string>();
        void AddKey(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !keyValues.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                keyValues.Add(value.Trim());
        }
        AddKey(companyCode);
        var internalId = await FindCompanyIdAsync(connection, companyCode, ct);
        AddKey(internalId?.ToString());
        if (keyValues.Count == 0)
            throw new InvalidOperationException("The active company could not be matched to the current database, so the dataset was not exported.");

        string CompanyPredicate(string alias)
        {
            var parts = new List<string>();
            for (var i = 0; i < keyValues.Count; i++)
            {
                var name = $"@scope{i}";
                parameters.Add((name, keyValues[i]));
                parts.Add($"CAST({alias}{QuoteIdentifier("company_id")} AS TEXT) = {name}");
            }
            return "(" + string.Join(" OR ", parts) + ")";
        }

        string ExtraPredicates(string alias, IReadOnlyList<string> scopeColumns)
        {
            var extra = string.Empty;
            var kinds = DocKindsFor(option.Filter);
            if (kinds.Length > 0 && scopeColumns.Contains("doc_kind", StringComparer.OrdinalIgnoreCase))
            {
                var names = new List<string>();
                for (var i = 0; i < kinds.Length; i++)
                {
                    var name = $"@kind{i}";
                    parameters.Add((name, kinds[i]));
                    names.Add(name);
                }
                extra += $" AND {alias}{QuoteIdentifier("doc_kind")} IN ({string.Join(", ", names)})";
            }
            var partyKind = PartyKindFor(option.Filter);
            if (partyKind != null && scopeColumns.Contains("kind", StringComparer.OrdinalIgnoreCase))
            {
                parameters.Add(("@partyKind", partyKind));
                extra += $" AND {alias}{QuoteIdentifier("kind")} = @partyKind";
            }
            return extra;
        }

        var table = QuoteIdentifier(option.TableName);
        if (ParentScopeByTable.TryGetValue(option.TableName, out var parent))
        {
            var parentColumns = await GetColumnNamesAsync(connection, parent.ParentTable, ct);
            if (!parentColumns.Contains("company_id", StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The dataset '{option.Key}' cannot be safely restricted to the active company, so it was not exported.");
            var sql =
                $"SELECT l.* FROM {table} l JOIN {QuoteIdentifier(parent.ParentTable)} p " +
                $"ON p.{QuoteIdentifier(parent.ParentKey)} = l.{QuoteIdentifier(parent.ChildKey)} " +
                $"WHERE {CompanyPredicate("p.")}{ExtraPredicates("p.", parentColumns)}";
            return (sql, parameters);
        }

        if (option.TableName.Equals("companies", StringComparison.OrdinalIgnoreCase))
        {
            parameters.Add(("@scopeCode", companyCode.Trim()));
            var byId = CompanyPredicate(string.Empty);
            return ($"SELECT * FROM {table} WHERE {QuoteIdentifier("company_code")} = @scopeCode OR {byId}", parameters);
        }

        if (!columns.Contains("company_id", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The dataset '{option.Key}' cannot be safely restricted to the active company, so it was not exported.");

        return ($"SELECT * FROM {table} WHERE {CompanyPredicate(string.Empty)}{ExtraPredicates(string.Empty, columns)}", parameters);
    }

    private static bool IsCompanyIndependent(string tableName)
        // Step 10: after the catalog repair, NO catalog dataset is
        // company-independent. currencies and units_of_measure carry a
        // NOT NULL company_id (031 / 001) and are company-scoped like
        // everything else; account_types / tax_codes no longer exist.
        // The companies dataset is handled by its own branch in
        // ImportRowsAsync (its company_id is its own primary key).
        => tableName.Equals("companies", StringComparison.OrdinalIgnoreCase);

    // Phase 4: identifier quoting is centralized in SqlIdentifier, which rejects
    // any identifier outside [A-Za-z_][A-Za-z0-9_]* instead of merely escaping it.
    // All callers pass catalog- or schema-derived names (export catalog seeds,
    // PRAGMA / information_schema column reads), never raw user input.
    private static string QuoteIdentifier(string value)
        => SqlIdentifier.Quote(value);

    private static RptColType GetColumnType(string columnName, Type valueType)
    {
        var name = columnName.ToLowerInvariant();
        if (valueType == typeof(bool) || name.StartsWith("is_", StringComparison.Ordinal) || name.StartsWith("allow_", StringComparison.Ordinal))
            return RptColType.Boolean;
        if (valueType == typeof(DateTime) || name.EndsWith("_at", StringComparison.Ordinal) || name.EndsWith("_date", StringComparison.Ordinal))
            return name.EndsWith("_at", StringComparison.Ordinal) ? RptColType.DateTime : RptColType.Date;
        if (valueType == typeof(byte) || valueType == typeof(short) || valueType == typeof(int) || valueType == typeof(long))
            return RptColType.WholeNumber;
        if (valueType == typeof(decimal) || valueType == typeof(double) || valueType == typeof(float))
            return name.Contains("amount", StringComparison.Ordinal) || name.Contains("price", StringComparison.Ordinal) || name.Contains("cost", StringComparison.Ordinal) || name.Contains("balance", StringComparison.Ordinal) || name.Contains("total", StringComparison.Ordinal)
                ? RptColType.Money
                : RptColType.FractionalNumber;
        return RptColType.Text;
    }

    private static string Humanize(string value)
    {
        var words = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words.ToLowerInvariant());
    }

    private static string ToSnakeCase(string value)
        => Regex.Replace(value, "([a-z0-9])([A-Z])", "$1_$2").Replace('-', '_').ToLowerInvariant();
}
