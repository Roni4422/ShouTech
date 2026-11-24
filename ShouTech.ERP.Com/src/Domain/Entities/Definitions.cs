namespace ShouTech.Domain.Entities;

/// <summary>Value kind of a definition-set column (drives editor control, validation and SQL binding).</summary>
public enum DefColumnType
{
    Text,
    Number,
    Integer,
    Bool,
    Date
}

/// <summary>
/// One editable column of a definition set. <see cref="Name"/> is the physical
/// column name and comes ONLY from the static <see cref="DefSets"/> registry —
/// never from user input — so it is safe to interpolate as a SQL identifier
/// after the repository's allow-list check (Phase 4 SqlIdentifier pattern,
/// re-implemented provider-neutrally in Infrastructure because the app-layer
/// helper is not visible to the src projects).
/// </summary>
public sealed record DefColumn(
    string Name,
    DefColumnType Type,
    bool Required = false,
    bool IsLookup = false,
    IReadOnlyList<string>? AllowedValues = null,
    /// <summary>True when the schema column is NOT NULL with a default (e.g. DEFAULT 0 / ''): an unset value binds the type zero instead of NULL.</summary>
    bool HasDefault = false);

/// <summary>
/// Descriptor for one definition set: which table backs it, which columns are
/// editable, which columns form the company-scoped uniqueness key, the fixed
/// (discriminator) values that scope the set inside a shared table, and the
/// PermCat permission codes that gate viewing/managing it.
/// </summary>
public sealed class DefSet
{
    public required string Key { get; init; }
    public required string Table { get; init; }
    /// <summary>Primary-key column. "id" for migration-036 tables; legacy tables differ.</summary>
    public string IdColumn { get; init; } = "id";
    /// <summary>True when the PK is a TEXT guid assigned by the app; false for AUTOINCREMENT/BIGSERIAL keys.</summary>
    public bool IdIsText { get; init; } = true;
    public required IReadOnlyList<DefColumn> Columns { get; init; }
    /// <summary>Columns (in addition to FixedValues keys) forming the uniqueness key within a company.</summary>
    public IReadOnlyList<string> UniqueKeys { get; init; } = Array.Empty<string>();
    /// <summary>Discriminator values written on every insert and matched on every read (e.g. set_kind).</summary>
    public IReadOnlyDictionary<string, string> FixedValues { get; init; } = new Dictionary<string, string>();
    public required string PermissionView { get; init; }
    public required string PermissionManage { get; init; }
    /// <summary>Extra permission codes that also grant manage (e.g. price-list create/update for customer prices).</summary>
    public IReadOnlyList<string> ExtraManagePermissions { get; init; } = Array.Empty<string>();
    public bool HasSoftDelete { get; init; } = true;
    public bool HasActive { get; init; } = true;
    /// <summary>False for legacy tables (units_of_measure, material_attributes) that carry no created_by/updated_at columns.</summary>
    public bool HasAuditColumns { get; init; } = true;
    /// <summary>Also list shared global rows stored with company_id '' (payment-method seeds).</summary>
    public bool IncludeGlobalRows { get; init; }
    public string OrderByColumn { get; init; } = "code";
}

/// <summary>One row of a definition set as exchanged between repository, service and window.</summary>
public sealed class DefRow
{
    public string Id { get; set; } = string.Empty;
    /// <summary>The company that owns the row ('' for shared global seed rows).</summary>
    public string CompanyId { get; set; } = string.Empty;
    public Dictionary<string, object?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Static registry of every definition set served by the generic framework
/// (ribbon completion F1). Keys are the routing strings used by
/// DefManagerWin.For(string).
/// </summary>
public static class DefSets
{
    public const string VoucherTypes = "VoucherTypes";
    public const string SecurityTypes = "SecurityTypes";
    public const string PaymentTypes = "PaymentTypes";
    public const string InvoiceDefinitions = "InvoiceDefinitions";
    public const string OrderDefinitions = "OrderDefinitions";
    public const string PaymentMethods = "PaymentMethods";
    public const string PosTerminals = "PosTerminals";
    public const string Areas = "Areas";
    public const string PartyTargets = "PartyTargets";
    public const string RepTargets = "RepTargets";
    public const string MaterialDiscounts = "MaterialDiscounts";
    public const string CustomerDiscounts = "CustomerDiscounts";
    public const string PosDiscounts = "PosDiscounts";
    public const string CustomerPrices = "CustomerPrices";
    public const string Devices = "Devices";
    public const string DeviceRoutes = "DeviceRoutes";
    public const string Drivers = "Drivers";
    public const string DeliveryPrices = "DeliveryPrices";
    public const string CustomFields = "CustomFields";
    public const string UnitsOfMeasure = "UnitsOfMeasure";
    public const string MaterialSpecs = "MaterialSpecs";

    private static DefColumn T(string name, bool required = false, bool lookup = false, IReadOnlyList<string>? allowed = null)
        => new(name, DefColumnType.Text, required, lookup, allowed);
    private static DefColumn N(string name, bool required = false) => new(name, DefColumnType.Number, required);
    private static DefColumn I(string name, bool required = false) => new(name, DefColumnType.Integer, required);
    private static DefColumn B(string name) => new(name, DefColumnType.Bool);
    private static DefColumn D(string name, bool required = false) => new(name, DefColumnType.Date, required);

    private static DefSet DocTypes(string key, string kind, string view, string manage) => new()
    {
        Key = key,
        Table = "doc_type_definitions",
        Columns = new[] { T("code", required: true), T("name_en", required: true), T("name_ar"), T("gl_account_code", lookup: true) },
        UniqueKeys = new[] { "code" },
        FixedValues = new Dictionary<string, string> { ["set_kind"] = kind },
        PermissionView = view,
        PermissionManage = manage,
        OrderByColumn = "code"
    };

    private static DefSet Targets(string key, string ownerKind, string view, string manage) => new()
    {
        Key = key,
        Table = "targets",
        Columns = new[]
        {
            T("owner_code", required: true, lookup: true),
            T("period_kind", required: true, allowed: new[] { "M", "Q", "Y" }),
            D("period_start", required: true),
            N("target_amount", required: true)
        },
        UniqueKeys = Array.Empty<string>(),
        FixedValues = new Dictionary<string, string> { ["owner_kind"] = ownerKind },
        PermissionView = view,
        PermissionManage = manage,
        HasActive = false,
        HasSoftDelete = false,
        OrderByColumn = "owner_code"
    };

    private static DefSet Discounts(string key, string scope, string view, string manage) => new()
    {
        Key = key,
        Table = "discount_rules",
        Columns = new[] { T("target_code", required: true, lookup: true), N("discount_pct", required: true), N("min_qty") with { HasDefault = true } },
        UniqueKeys = Array.Empty<string>(),
        FixedValues = new Dictionary<string, string> { ["scope"] = scope },
        PermissionView = view,
        PermissionManage = manage,
        OrderByColumn = "target_code"
    };

    public static readonly IReadOnlyDictionary<string, DefSet> All = new Dictionary<string, DefSet>(StringComparer.OrdinalIgnoreCase)
    {
        [VoucherTypes] = DocTypes(VoucherTypes, "VOUCHER", "VCH.VOUCHER.VIEW", "VCH.VOUCHER.UPDATE"),
        [SecurityTypes] = DocTypes(SecurityTypes, "SECURITY", "VCH.SECURITY.VIEW", "VCH.SECURITY.MANAGE"),
        [PaymentTypes] = DocTypes(PaymentTypes, "PAYMENT", "VCH.VOUCHER.VIEW", "VCH.VOUCHER.UPDATE"),
        [InvoiceDefinitions] = DocTypes(InvoiceDefinitions, "INVOICE", "SALES.INVOICE.VIEW", "SALES.INVOICE.UPDATE"),
        [OrderDefinitions] = DocTypes(OrderDefinitions, "ORDER", "SALES.ORDER.VIEW", "SALES.ORDER.UPDATE"),
        [PaymentMethods] = new DefSet
        {
            Key = PaymentMethods,
            Table = "payment_methods",
            Columns = new[]
            {
                T("code", required: true), T("name_en", required: true), T("name_ar"),
                T("kind", required: true, allowed: new[] { "Cash", "BankTransfer", "Cheque", "Card", "Other" }),
                T("gl_account_code", lookup: true), T("settlement_bank_code", lookup: true),
                N("commission_pct") with { HasDefault = true }, T("commission_account_code", lookup: true),
                B("requires_reference"), B("is_pos_enabled"), I("pos_sort_order") with { HasDefault = true }
            },
            UniqueKeys = new[] { "code" },
            PermissionView = "VCH.VOUCHER.VIEW",
            PermissionManage = "VCH.PAYMENT_METHOD.MANAGE",
            IncludeGlobalRows = true,
            OrderByColumn = "code"
        },
        [PosTerminals] = new DefSet
        {
            Key = PosTerminals,
            Table = "pos_terminals",
            Columns = new[] { T("code", required: true), T("name_en", required: true), T("name_ar"), T("warehouse_code", lookup: true), T("cash_account_code", lookup: true) },
            UniqueKeys = new[] { "code" },
            PermissionView = "POS.TERMINAL.VIEW",
            PermissionManage = "POS.TERMINAL.MANAGE",
            OrderByColumn = "code"
        },
        [Areas] = new DefSet
        {
            Key = Areas,
            Table = "areas",
            Columns = new[] { T("code", required: true), T("name_en", required: true), T("name_ar"), T("parent_id") },
            UniqueKeys = new[] { "code" },
            PermissionView = "DELIVERY.ORDER.VIEW",
            PermissionManage = "REP.ROUTE.MANAGE",
            OrderByColumn = "code"
        },
        [PartyTargets] = Targets(PartyTargets, "PARTY", "CRM.CUSTOMER_TARGET.VIEW", "CRM.CUSTOMER_TARGET.MANAGE"),
        [RepTargets] = Targets(RepTargets, "REP", "REP.TARGET.VIEW", "REP.TARGET.MANAGE"),
        [MaterialDiscounts] = Discounts(MaterialDiscounts, "MATERIAL", "INV.DISCOUNT.VIEW", "INV.DISCOUNT.MANAGE"),
        [CustomerDiscounts] = Discounts(CustomerDiscounts, "CUSTOMER", "CRM.CUSTOMER_DISCOUNT.VIEW", "CRM.CUSTOMER_DISCOUNT.MANAGE"),
        [PosDiscounts] = Discounts(PosDiscounts, "POS", "POS.DISCOUNT.MANAGE", "POS.DISCOUNT.MANAGE"),
        [CustomerPrices] = new DefSet
        {
            Key = CustomerPrices,
            Table = "customer_prices",
            Columns = new[] { T("party_code", required: true, lookup: true), T("material_code", required: true, lookup: true), N("price", required: true) },
            UniqueKeys = new[] { "party_code", "material_code" },
            PermissionView = "CRM.CUSTOMER_PRICING.VIEW",
            PermissionManage = "CRM.CUSTOMER_PRICING.MANAGE",
            ExtraManagePermissions = new[] { "INV.PRICELIST.CREATE", "INV.PRICELIST.UPDATE" },
            OrderByColumn = "party_code"
        },
        [Devices] = new DefSet
        {
            Key = Devices,
            Table = "handheld_devices",
            Columns = new[] { T("code", required: true), T("name_en", required: true), T("name_ar"), T("rep_code", lookup: true), T("device_serial") },
            UniqueKeys = new[] { "code" },
            PermissionView = "REP.DEVICE.VIEW",
            PermissionManage = "REP.DEVICE.MANAGE",
            OrderByColumn = "code"
        },
        [DeviceRoutes] = new DefSet
        {
            Key = DeviceRoutes,
            Table = "device_routes",
            Columns = new[]
            {
                T("device_code", required: true, lookup: true), T("rep_code", lookup: true), T("area_code", lookup: true),
                I("weekday", required: true), I("visit_order") with { HasDefault = true }, T("party_code", lookup: true)
            },
            UniqueKeys = Array.Empty<string>(),
            PermissionView = "REP.ROUTE.VIEW",
            PermissionManage = "REP.ROUTE.MANAGE",
            HasActive = false,
            HasSoftDelete = false,
            OrderByColumn = "device_code"
        },
        [Drivers] = new DefSet
        {
            Key = Drivers,
            Table = "drivers",
            Columns = new[] { T("code", required: true), T("name_en", required: true), T("name_ar"), T("phone"), T("vehicle_no"), T("license_no") },
            UniqueKeys = new[] { "code" },
            PermissionView = "DELIVERY.DRIVER.VIEW",
            PermissionManage = "DELIVERY.DRIVER.MANAGE",
            OrderByColumn = "code"
        },
        [DeliveryPrices] = new DefSet
        {
            Key = DeliveryPrices,
            Table = "delivery_prices",
            Columns = new[] { T("area_code", required: true, lookup: true), N("price", required: true), N("min_order") with { HasDefault = true } },
            UniqueKeys = new[] { "area_code" },
            PermissionView = "DELIVERY.PRICING.VIEW",
            PermissionManage = "DELIVERY.PRICING.MANAGE",
            OrderByColumn = "area_code"
        },
        [CustomFields] = new DefSet
        {
            Key = CustomFields,
            Table = "custom_field_definitions",
            Columns = new[]
            {
                T("entity_kind", required: true), T("field_code", required: true), T("name_en", required: true), T("name_ar"),
                T("field_type", required: true, allowed: new[] { "TEXT", "NUMBER", "DATE", "BOOL" }), B("is_required")
            },
            UniqueKeys = new[] { "entity_kind", "field_code" },
            PermissionView = "SYS.CUSTOM_FIELDS.MANAGE",
            PermissionManage = "SYS.CUSTOM_FIELDS.MANAGE",
            OrderByColumn = "entity_kind"
        },
        // Existing table (migration 001): uom_id INTEGER PK, company_id, uom_code,
        // is_active, is_deleted — no name columns exist, so the set exposes the
        // code only. PostgreSQL stores company_id as uuid there (legacy schema);
        // the repository binds the company string as-is, matching SQLite.
        [UnitsOfMeasure] = new DefSet
        {
            Key = UnitsOfMeasure,
            Table = "units_of_measure",
            IdColumn = "uom_id",
            IdIsText = false,
            Columns = new[] { T("uom_code", required: true) },
            UniqueKeys = new[] { "uom_code" },
            PermissionView = "SYS.SETTINGS.VIEW",
            PermissionManage = "SYS.SETTINGS.UPDATE",
            HasAuditColumns = false,
            OrderByColumn = "uom_code"
        },
        // Existing table (migration 013/015): attr_id INTEGER PK, company_id,
        // material_id, attr_key, attr_value, attr_group, sort_order — the table
        // has NO is_active / is_deleted columns, so this set hard-deletes and
        // has no activate toggle (documented deviation from the generic shape).
        [MaterialSpecs] = new DefSet
        {
            Key = MaterialSpecs,
            Table = "material_attributes",
            IdColumn = "attr_id",
            IdIsText = false,
            Columns = new[]
            {
                new DefColumn("material_id", DefColumnType.Integer, Required: true, IsLookup: true),
                T("attr_key", required: true), T("attr_value") with { HasDefault = true }, T("attr_group") with { HasDefault = true }, I("sort_order") with { HasDefault = true }
            },
            UniqueKeys = new[] { "material_id", "attr_key" },
            PermissionView = "SYS.SETTINGS.VIEW",
            PermissionManage = "SYS.SETTINGS.UPDATE",
            HasActive = false,
            HasSoftDelete = false,
            HasAuditColumns = false,
            OrderByColumn = "material_id"
        }
    };

    public static DefSet Get(string key)
    {
        if (key is not null && All.TryGetValue(key, out var set))
            return set;
        throw new ArgumentException($"Unknown definition set '{key}'.", nameof(key));
    }
}

// ---------------------------------------------------------------------------
// Dedicated-module entities (installments, loyalty, delivery)
// ---------------------------------------------------------------------------

public static class InstallmentStatuses
{
    public const string Active = "Active";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public static readonly IReadOnlyList<string> All = new[] { Active, Completed, Cancelled };
}

public static class ScheduleStatuses
{
    public const string Due = "Due";
    public const string Partial = "Partial";
    public const string Paid = "Paid";
    public static readonly IReadOnlyList<string> All = new[] { Due, Partial, Paid };
}

/// <summary>An installment plan header; its schedule rows sum to TotalAmount - DownPayment.</summary>
public sealed class InstallmentPlan
{
    public string Id { get; set; } = string.Empty;
    public string PartyCode { get; set; } = string.Empty;
    public string? DocRef { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal DownPayment { get; set; }
    public int InstallmentCount { get; set; }
    public DateOnly StartDate { get; set; }
    public string Status { get; set; } = InstallmentStatuses.Active;
    public string? Notes { get; set; }
    public decimal PaidTotal { get; set; }
    public decimal Remaining => TotalAmount - DownPayment - PaidTotal;
}

public sealed class InstallmentScheduleRow
{
    public string Id { get; set; } = string.Empty;
    public string InstallmentId { get; set; } = string.Empty;
    public int Seq { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal Amount { get; set; }
    public decimal PaidAmount { get; set; }
    public DateOnly? PaidDate { get; set; }
    public string Status { get; set; } = ScheduleStatuses.Due;
    public decimal Remaining => Amount - PaidAmount;
}

public sealed class LoyaltyAccount
{
    public string Id { get; set; } = string.Empty;
    public string PartyCode { get; set; } = string.Empty;
    public decimal PointsBalance { get; set; }
}

public sealed class LoyaltyEntry
{
    public string Id { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public DateOnly EntryDate { get; set; }
    public decimal Points { get; set; }
    public string? DocRef { get; set; }
    public string? Notes { get; set; }
}

public static class DeliveryStatuses
{
    public const string Pending = "Pending";
    public const string Assigned = "Assigned";
    public const string Delivered = "Delivered";
    public const string Cancelled = "Cancelled";
    public const string Returned = "Returned";
    public static readonly IReadOnlyList<string> All = new[] { Pending, Assigned, Delivered, Cancelled, Returned };

    /// <summary>Allowed forward transitions (status flow enforced in the service).
    /// Returned (sales cycle W4): a dispatched (Assigned) or completed
    /// (Delivered) run whose goods came back; terminal like Cancelled.</summary>
    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (Pending, Assigned) => true,
        (Pending, Cancelled) => true,
        (Assigned, Delivered) => true,
        (Assigned, Cancelled) => true,
        (Assigned, Pending) => true,
        (Assigned, Returned) => true,
        (Delivered, Returned) => true,
        _ => false
    };
}

public sealed class DeliveryRequest
{
    public string Id { get; set; } = string.Empty;
    public int DocNo { get; set; }
    public DateOnly RequestDate { get; set; }
    public string? PartyCode { get; set; }
    public string? Address { get; set; }
    public string? AreaCode { get; set; }
    public string? DriverCode { get; set; }
    public string Status { get; set; } = DeliveryStatuses.Pending;
    public decimal Amount { get; set; }
    public string? Notes { get; set; }

    // Sales cycle W4 (migration 096): traceability to the shipped sales
    // document, the vehicle used, and proof of delivery.
    /// <summary>sales_documents.doc_id of the shipped document, when linked.</summary>
    public long? SalesDocId { get; set; }
    /// <summary>Kind of the shipped sales document ('DLV').</summary>
    public string? SalesDocKind { get; set; }
    /// <summary>Vehicle that carried the delivery; defaults from the assigned driver's card, editable.</summary>
    public string? VehicleNo { get; set; }
    /// <summary>Proof-of-delivery timestamp (ISO text), stamped when the request is delivered.</summary>
    public string? DeliveredAt { get; set; }
    /// <summary>Name of the person who received the goods (proof of delivery).</summary>
    public string? ReceivedBy { get; set; }
    /// <summary>Optional receiver signature image (PNG bytes).</summary>
    public byte[]? SignatureImage { get; set; }
}

public sealed class DriverSettlement
{
    public string Id { get; set; } = string.Empty;
    public string DriverCode { get; set; } = string.Empty;
    public DateOnly SettleDate { get; set; }
    public decimal CollectedAmount { get; set; }
    public decimal FeesAmount { get; set; }
    public decimal NetAmount { get; set; }
    public string Status { get; set; } = "Open";
}
