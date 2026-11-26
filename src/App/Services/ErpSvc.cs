using System;
using System.Collections.Generic;
using System.Linq;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Domain.Enums;

namespace ShouTech.Application.Services;

public sealed class ErpSvc
{
    private readonly List<Customer> _customers = new();
    private readonly List<Vendor> _vendors = new();
    private readonly List<InventoryItem> _items = new();
    private readonly List<SalesInvoice> _invoices = new();

    public IReadOnlyCollection<Customer> Customers => _customers.AsReadOnly();
    public IReadOnlyCollection<Vendor> Vendors => _vendors.AsReadOnly();
    public IReadOnlyCollection<InventoryItem> Items => _items.AsReadOnly();
    public IReadOnlyCollection<SalesInvoice> Invoices => _invoices.AsReadOnly();

    public Customer CreateCustomer(string code, string name, string? phoneNumber = null, string? email = null, string currencyCode = "SAR")
    {
        var customer = Customer.Create(code, name, phoneNumber, email, currencyCode);
        if (_customers.Any(x => string.Equals(x.Code, customer.Code, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Customer '{customer.Code}' already exists.");

        _customers.Add(customer);
        return customer;
    }

    public Vendor CreateVendor(string code, string name, string? phoneNumber = null, string? email = null, string currencyCode = "SAR")
    {
        var vendor = Vendor.Create(code, name, phoneNumber, email, currencyCode);
        if (_vendors.Any(x => string.Equals(x.Code, vendor.Code, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Vendor '{vendor.Code}' already exists.");

        _vendors.Add(vendor);
        return vendor;
    }

    public InventoryItem CreateItem(string code, string name, decimal unitPrice, decimal stockOnHand, string unit = "PCS", decimal reorderLevel = 0m)
    {
        var item = InventoryItem.Create(code, name, unitPrice, stockOnHand, unit, reorderLevel);
        if (_items.Any(x => string.Equals(x.Code, item.Code, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Item '{item.Code}' already exists.");

        _items.Add(item);
        return item;
    }

    public SalesInvoice CreateSalesInvoice(Guid customerId, string number, DateOnly invoiceDate, string currencyCode = "SAR")
    {
        if (!_customers.Any(x => x.Id == customerId))
            throw new InvalidOperationException("Customer was not found.");

        var invoice = SalesInvoice.Create(customerId, number, invoiceDate, currencyCode);
        if (_invoices.Any(x => string.Equals(x.Number, invoice.Number, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Invoice '{invoice.Number}' already exists.");

        _invoices.Add(invoice);
        return invoice;
    }

    public SalesInvoice AddInvoiceLine(Guid invoiceId, Guid itemId, string description, decimal quantity, decimal unitPrice, decimal taxRate = 0m)
    {
        var invoice = _invoices.SingleOrDefault(x => x.Id == invoiceId)
            ?? throw new InvalidOperationException("Invoice was not found.");
        var item = _items.SingleOrDefault(x => x.Id == itemId)
            ?? throw new InvalidOperationException("Item was not found.");

        if (invoice.Status != InvoiceStatus.Draft)
            throw new InvalidOperationException("Only draft invoices can receive new lines.");

        invoice.AddLine(item.Id, item.Code, description, quantity, unitPrice, taxRate);
        return invoice;
    }

    public SalesInvoice PostInvoice(Guid invoiceId)
    {
        var invoice = _invoices.SingleOrDefault(x => x.Id == invoiceId)
            ?? throw new InvalidOperationException("Invoice was not found.");

        foreach (var line in invoice.Lines)
        {
            var item = _items.SingleOrDefault(x => x.Id == line.ItemId)
                ?? throw new InvalidOperationException($"Item '{line.ItemCode}' is missing from stock.");

            if (item.StockOnHand < line.Quantity)
                throw new InvalidOperationException($"Insufficient stock for item '{item.Code}'. Required {line.Quantity}, available {item.StockOnHand}.");

            item.AdjustStock(-line.Quantity, $"Sold on invoice {invoice.Number}");
        }

        invoice.Post();
        return invoice;
    }

    public InventoryItem AdjustStock(Guid itemId, decimal delta, Guid modifiedBy)
    {
        var item = _items.SingleOrDefault(x => x.Id == itemId)
            ?? throw new InvalidOperationException("Item was not found.");

        item.AdjustStock(delta, "Manual adjustment", modifiedBy);
        return item;
    }
}

// ---- merged from src/App/Services/UsrSvc.cs ----
public sealed class UsrSvc
{
    private readonly IUsrRepo _repo;   // The repository contract will be wired up later.
    private readonly ILicProv _lic;

    public UsrSvc(IUsrRepo repo, ILicProv lic)
    {
        _repo = repo;
        _lic = lic;
    }

    public async Task<Usr> CreateAsync(
        string userName,
        string displayName,
        Guid createdBy,
        string? email = null,
        string? mobile = null,
        string lang = "en",
        CancellationToken ct = default)
    {
        var lic = await _lic.ValidateAsync(ct);
        if (!lic.IsUsable)
            throw new InvalidOperationException("License is not usable.");


        var user = Usr.Create(userName, displayName, createdBy, email, mobile, lang);
        await _repo.AddAsync(user, ct);
        return user;
    }

    public async Task SetActiveAsync(Guid id, bool isActive, Guid modifiedBy, CancellationToken ct = default)
    {
        var user = await _repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("User not found.");

        user.SetActive(isActive, modifiedBy);
        await _repo.UpdateAsync(user, ct);
    }
}

// ---- merged from src/App/Services/FYearSvc.cs ----
public sealed class FYearSvc
{
    private readonly IFYearRepo _repo;
    private readonly IComRepo _comRepo;
    private readonly ILicProv _lic;

    public FYearSvc(IFYearRepo repo, IComRepo comRepo, ILicProv lic)
    {
        _repo = repo;
        _comRepo = comRepo;
        _lic = lic;
    }

    public async Task<FYear> CreateAsync(
        Guid companyId,
        string name,
        DateOnly start,
        DateOnly end,
        string dbName,
        Guid userId,
        bool setAsCurrent = false,
        CancellationToken ct = default)
    {
        var lic = await _lic.ValidateAsync(ct);
        if (!lic.IsUsable)
            throw new InvalidOperationException("License is not usable.");

        var company = await _comRepo.GetByIdAsync(companyId, false, ct)
            ?? throw new InvalidOperationException("Company not found.");

        var year = FYear.Create(companyId, name, start, end, dbName, userId, setAsCurrent);

        if (setAsCurrent)
        {
            await _repo.ClearCurrentForCompanyAsync(companyId, userId, ct);
        }

        await _repo.AddAsync(year, ct);
        return year;
    }

    public async Task CloseAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var year = await _repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Fiscal year not found.");

        if (year.IsClosed)
            throw new InvalidOperationException("Fiscal year is already closed.");

        year.Close(userId);
        await _repo.UpdateAsync(year, ct);
    }
}

// ---- merged from src/App/Services/LicSvc.cs ----
/// <summary>
/// Central commercial license gate.
/// Every write operation and every optional module must pass through this service.
/// </summary>
public sealed class LicSvc
{
    private readonly ILicProv _provider;

    public LicSvc(ILicProv provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task EnsureUsableAsync(CancellationToken ct = default)
    {
        var r = await _provider.ValidateAsync(ct);
        if (!r.IsUsable)
            throw new UnauthorizedAccessException(r.Message ?? "License is not valid.");
    }

    /// <summary>
    /// Blocks any create / edit / post when license is Viewer or unusable.
    /// </summary>
    public async Task EnsureCanWriteAsync(CancellationToken ct = default)
    {
        var r = await _provider.ValidateAsync(ct);

        if (!r.IsUsable)
            throw new UnauthorizedAccessException(r.Message ?? "License is not valid.");

        if (r.IsViewer || !_provider.CanWrite)
            throw new UnauthorizedAccessException("Read-only license (Viewer). Writing is not allowed.");
    }

    /// <summary>
    /// Ensures the requested module is present in the license.
    /// Core modules are automatically allowed for Core/Demo.
    /// </summary>
    public async Task EnsureModuleAsync(string moduleKey, CancellationToken ct = default)
    {
        var r = await _provider.ValidateAsync(ct);

        if (!r.IsUsable)
            throw new UnauthorizedAccessException(r.Message ?? "License is not valid.");

        if (!_provider.IsModuleEnabled(moduleKey))
            throw new UnauthorizedAccessException($"Module '{moduleKey}' is not licensed.");
    }

    public bool IsModuleEnabled(string moduleKey) => _provider.IsModuleEnabled(moduleKey);

    public async Task<LicResult> GetStatusAsync(CancellationToken ct = default)
        => await _provider.ValidateAsync(ct);
}

// ---- merged from src/App/Services/ErpPlan.cs ----
public sealed record ErpFeat(
    string Code,
    string Title,
    string Description,
    int Weight,
    bool IsCore,
    bool IsAutomation);

/// <summary>
/// Product-level planning helpers to keep ShouTech simpler, faster,
/// more powerful, and more modern than legacy ERP products.
/// </summary>
public sealed class ErpPlan
{
    private static readonly IReadOnlyList<ErpFeat> Feats = new[]
    {
        new ErpFeat("ACC", "Accounting", "Fast ledger, journals, and financial control", 100, true, false),
        new ErpFeat("INV", "Inventory", "Live stock, movements, and valuation", 98, true, false),
        new ErpFeat("SALES", "Sales", "Quick customer invoicing and order flow", 96, true, false),
        new ErpFeat("PURCH", "Purchases", "Vendor control and supplier purchasing", 94, true, false),
        new ErpFeat("DASH", "Dashboard", "Single-screen business overview for managers", 92, true, true),
        new ErpFeat("RPT", "Reporting", "Profit, cash flow, and KPI visibility", 90, true, true),
        new ErpFeat("POS", "POS", "Fast retail checkout and sales speed", 85, false, true),
        new ErpFeat("AI", "AI Insights", "Low-stock alerts and predictive insight", 75, false, true)
    };

    public IReadOnlyList<ErpFeat> Pri()
    {
        return Feats
            .OrderByDescending(x => x.Weight)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> ModSeq()
    {
        return Pri()
            .Select(x => x.Code)
            .ToList();
    }

    public IReadOnlyList<string> FastFlow()
    {
        return new[]
        {
            "Quick customer and item lookup",
            "One-click sales invoice workflow",
            "Low-stock and cash-alert warnings",
            "Fast posting with approval and audit trail",
            "Excel export in a single action"
        };
    }
}

// ---- merged from src/App/Services/ModMenuSvc.cs ----
/// <summary>
/// Builds the list of modules that should appear in the main ribbon/menu
/// according to the current license (exactly as local market leaders do).
/// </summary>
public sealed class ModMenuSvc
{
    private readonly ILicProv _lic;

    public ModMenuSvc(ILicProv lic)
    {
        _lic = lic ?? throw new ArgumentNullException(nameof(lic));
    }

    /// <summary>
    /// Returns the fastest modern launch sequence for day-to-day ERP work.
    /// This keeps the UI simpler and faster than legacy ERP products.
    /// </summary>
    public IReadOnlyList<string> GetModernLaunchSequence()
    {
        return new[]
        {
            ModKey.Acc,
            ModKey.Inv,
            ModKey.Sales,
            ModKey.Purch,
            ModKey.AdvRpt,
            ModKey.Pos,
            ModKey.Ai
        };
    }

    /// <summary>
    /// Returns ordered list of module keys that must be shown in the main UI.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetVisibleModulesAsync(CancellationToken ct = default)
    {
        var result = await _lic.ValidateAsync(ct);
        var list = new List<string>();

        // Demo: show core + optional for trial experience
        if (result.IsDemo)
        {
            list.AddRange(new[] { ModKey.Acc, ModKey.Inv, ModKey.Sales, ModKey.Purch });
            // optionally add more in demo if desired
            return list;
        }

        // Viewer or Core: always show core modules when license is usable
        if (result.IsUsable)
        {
            list.Add(ModKey.Acc);
            list.Add(ModKey.Inv);
            list.Add(ModKey.Sales);
            list.Add(ModKey.Purch);
        }

        // Optional modules – only if explicitly licensed
        void AddIf(string key)
        {
            if (_lic.IsModuleEnabled(key))
                list.Add(key);
        }

        AddIf(ModKey.Hr);
        AddIf(ModKey.Mfg);
        AddIf(ModKey.Pos);
        AddIf(ModKey.Ai);
        AddIf(ModKey.AdvRpt);
        AddIf(ModKey.MultiCur);
        AddIf(ModKey.WhiteLabel);

        return list;
    }
}

// ---- merged from src/App/Services/ComSvc.cs ----
/// <summary>
/// Application service for Company management.
/// Enforces business rules, licensing limits, and audit.
/// </summary>
public sealed class ComSvc
{
    private readonly IComRepo _repo;
    private readonly ILicProv _lic;

    public ComSvc(IComRepo repo, ILicProv lic)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _lic = lic ?? throw new ArgumentNullException(nameof(lic));
    }

    public async Task<Comp> CreateAsync(
        string code,
        string name,
        string nameSecondary,
        string currency,
        Guid userId,
        string? taxNumber = null,
        Guid? groupId = null,
        CancellationToken ct = default)
    {
        // Edition limit check (Starter = 1 company, Professional/Enterprise = more)
        var lic = await _lic.ValidateAsync(ct);
        if (!lic.IsUsable)
            throw new InvalidOperationException("License is not usable.");

        if (lic.Edition == LicEd.Core)
        {
            var existing = await _repo.GetAllAsync(activeOnly: false, ct);
            if (existing.Count >= 1)
                throw new InvalidOperationException("Starter edition allows only one company.");
        }

        if (await _repo.CodeExistsAsync(code, null, ct))
            throw new InvalidOperationException($"Company code '{code}' already exists.");

        var company = Comp.Create(code, name, nameSecondary, currency, userId, taxNumber, groupId);
        await _repo.AddAsync(company, ct);
        return company;
    }

    public async Task UpdateAsync(
        Guid id,
        string name,
        string nameSecondary,
        string? taxNumber,
        string currency,
        Guid? groupId,
        int sortOrder,
        string? notes,
        Guid userId,
        CancellationToken ct = default)
    {
        var company = await _repo.GetByIdAsync(id, false, ct)
            ?? throw new InvalidOperationException("Company not found.");

        company.Update(name, nameSecondary, taxNumber, currency, groupId, sortOrder, notes, userId);
        await _repo.UpdateAsync(company, ct);
    }

    public async Task SoftDeleteAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var company = await _repo.GetByIdAsync(id, false, ct)
            ?? throw new InvalidOperationException("Company not found.");

        // Prevent deleting the last active company in lower editions if needed
        await _repo.SoftDeleteAsync(id, userId, ct);
    }

    public async Task<IReadOnlyList<Comp>> GetAllAsync(bool activeOnly = true, CancellationToken ct = default)
    {
        return await _repo.GetAllAsync(activeOnly, ct);
    }

    public async Task<Comp?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _repo.GetByIdAsync(id, false, ct);
    }
}
