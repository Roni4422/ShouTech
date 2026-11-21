using System;
using System.Collections.Generic;
using System.Linq;

namespace ShouTech.Domain.Entities;

public sealed class Customer : EntBase
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? PhoneNumber { get; private set; }
    public string? Email { get; private set; }
    public string CurrencyCode { get; private set; } = "SAR";
    public bool IsActive { get; private set; } = true;

    private Customer() { }

    public static Customer Create(string code, string name, string? phoneNumber = null, string? email = null, string currencyCode = "SAR")
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Customer code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Customer name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            throw new ArgumentException("Currency code must be a valid 3-letter ISO code.", nameof(currencyCode));

        return new Customer
        {
            Code = code.Trim(),
            Name = name.Trim(),
            PhoneNumber = phoneNumber?.Trim(),
            Email = email?.Trim(),
            CurrencyCode = currencyCode.Trim().ToUpperInvariant()
        };
    }

    public void Update(string name, string? phoneNumber, string? email, string currencyCode, Guid modifiedBy)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Customer name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            throw new ArgumentException("Currency code must be a valid 3-letter ISO code.", nameof(currencyCode));

        Name = name.Trim();
        PhoneNumber = phoneNumber?.Trim();
        Email = email?.Trim();
        CurrencyCode = currencyCode.Trim().ToUpperInvariant();
        Touch(modifiedBy);
    }

    public void SetActive(bool isActive, Guid modifiedBy)
    {
        if (IsActive == isActive) return;
        IsActive = isActive;
        Touch(modifiedBy);
    }
}

public sealed class Vendor : EntBase
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? PhoneNumber { get; private set; }
    public string? Email { get; private set; }
    public string CurrencyCode { get; private set; } = "SAR";
    public bool IsActive { get; private set; } = true;

    private Vendor() { }

    public static Vendor Create(string code, string name, string? phoneNumber = null, string? email = null, string currencyCode = "SAR")
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Vendor code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Vendor name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            throw new ArgumentException("Currency code must be a valid 3-letter ISO code.", nameof(currencyCode));

        return new Vendor
        {
            Code = code.Trim(),
            Name = name.Trim(),
            PhoneNumber = phoneNumber?.Trim(),
            Email = email?.Trim(),
            CurrencyCode = currencyCode.Trim().ToUpperInvariant()
        };
    }

    public void Update(string name, string? phoneNumber, string? email, string currencyCode, Guid modifiedBy)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Vendor name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            throw new ArgumentException("Currency code must be a valid 3-letter ISO code.", nameof(currencyCode));

        Name = name.Trim();
        PhoneNumber = phoneNumber?.Trim();
        Email = email?.Trim();
        CurrencyCode = currencyCode.Trim().ToUpperInvariant();
        Touch(modifiedBy);
    }

    public void SetActive(bool isActive, Guid modifiedBy)
    {
        if (IsActive == isActive) return;
        IsActive = isActive;
        Touch(modifiedBy);
    }
}

public sealed class InventoryItem : EntBase
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Unit { get; private set; } = "PCS";
    public decimal UnitPrice { get; private set; }
    public decimal StockOnHand { get; private set; }
    public decimal ReorderLevel { get; private set; }
    public bool IsActive { get; private set; } = true;

    private InventoryItem() { }

    public static InventoryItem Create(string code, string name, decimal unitPrice, decimal stockOnHand, string unit = "PCS", decimal reorderLevel = 0m)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Item code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Item name is required.", nameof(name));
        if (unitPrice < 0m)
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "Unit price cannot be negative.");
        if (stockOnHand < 0m)
            throw new ArgumentOutOfRangeException(nameof(stockOnHand), "Stock on hand cannot be negative.");
        if (reorderLevel < 0m)
            throw new ArgumentOutOfRangeException(nameof(reorderLevel), "Reorder level cannot be negative.");

        return new InventoryItem
        {
            Code = code.Trim(),
            Name = name.Trim(),
            Unit = string.IsNullOrWhiteSpace(unit) ? "PCS" : unit.Trim(),
            UnitPrice = unitPrice,
            StockOnHand = stockOnHand,
            ReorderLevel = reorderLevel,
            IsActive = true
        };
    }

    public void Update(string name, decimal unitPrice, string unit, decimal reorderLevel, Guid modifiedBy)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Item name is required.", nameof(name));
        if (unitPrice < 0m)
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "Unit price cannot be negative.");
        if (reorderLevel < 0m)
            throw new ArgumentOutOfRangeException(nameof(reorderLevel), "Reorder level cannot be negative.");

        Name = name.Trim();
        UnitPrice = unitPrice;
        Unit = string.IsNullOrWhiteSpace(unit) ? Unit : unit.Trim();
        ReorderLevel = reorderLevel;
        Touch(modifiedBy);
    }

    public void AdjustStock(decimal delta, string reason, Guid? modifiedBy = null)
    {
        if (delta == 0m) return;
        if (StockOnHand + delta < 0m)
            throw new InvalidOperationException($"Insufficient stock for item '{Code}'.");

        StockOnHand += delta;
        if (modifiedBy is not null)
            Touch(modifiedBy.Value);
    }

    public void SetActive(bool isActive, Guid modifiedBy)
    {
        if (IsActive == isActive) return;
        IsActive = isActive;
        Touch(modifiedBy);
    }
}

public sealed class SalesInvoiceLine
{
    public Guid ItemId { get; }
    public string ItemCode { get; }
    public string Description { get; }
    public decimal Quantity { get; }
    public decimal UnitPrice { get; }
    public decimal TaxRate { get; }

    public decimal NetAmount => Quantity * UnitPrice;
    public decimal TaxAmount => NetAmount * (TaxRate / 100m);
    public decimal TotalAmount => NetAmount + TaxAmount;

    public SalesInvoiceLine(Guid itemId, string itemCode, string description, decimal quantity, decimal unitPrice, decimal taxRate = 0m)
    {
        if (itemId == Guid.Empty)
            throw new ArgumentException("Item is required.", nameof(itemId));
        if (string.IsNullOrWhiteSpace(itemCode))
            throw new ArgumentException("Item code is required.", nameof(itemCode));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Line description is required.", nameof(description));
        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        if (unitPrice < 0m)
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "Unit price cannot be negative.");
        if (taxRate < 0m)
            throw new ArgumentOutOfRangeException(nameof(taxRate), "Tax rate cannot be negative.");

        ItemId = itemId;
        ItemCode = itemCode.Trim();
        Description = description.Trim();
        Quantity = quantity;
        UnitPrice = unitPrice;
        TaxRate = taxRate;
    }
}

public sealed class SalesInvoice : EntBase
{
    private readonly List<SalesInvoiceLine> _lines = new();

    public Guid CustomerId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public DateOnly InvoiceDate { get; private set; }
    public string CurrencyCode { get; private set; } = "SAR";
    public InvoiceStatus Status { get; private set; } = InvoiceStatus.Draft;
    public IReadOnlyCollection<SalesInvoiceLine> Lines => _lines.AsReadOnly();

    public decimal Subtotal => _lines.Sum(x => x.NetAmount);
    public decimal TaxTotal => _lines.Sum(x => x.TaxAmount);
    public decimal Total => Subtotal + TaxTotal;

    private SalesInvoice() { }

    public static SalesInvoice Create(Guid customerId, string number, DateOnly invoiceDate, string currencyCode = "SAR")
    {
        if (customerId == Guid.Empty)
            throw new ArgumentException("Customer is required.", nameof(customerId));
        if (string.IsNullOrWhiteSpace(number))
            throw new ArgumentException("Invoice number is required.", nameof(number));
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            throw new ArgumentException("Currency code must be a valid 3-letter ISO code.", nameof(currencyCode));

        return new SalesInvoice
        {
            CustomerId = customerId,
            Number = number.Trim(),
            InvoiceDate = invoiceDate,
            CurrencyCode = currencyCode.Trim().ToUpperInvariant()
        };
    }

    public void AddLine(Guid itemId, string itemCode, string description, decimal quantity, decimal unitPrice, decimal taxRate = 0m)
    {
        _lines.Add(new SalesInvoiceLine(itemId, itemCode, description, quantity, unitPrice, taxRate));
    }

    public void Validate()
    {
        if (_lines.Count == 0)
            throw new InvalidOperationException("Invoice must contain at least one line.");

        if (Total <= 0m)
            throw new InvalidOperationException("Invoice total must be greater than zero.");
    }

    public void Post()
    {
        if (Status != InvoiceStatus.Draft)
            throw new InvalidOperationException("Only draft invoices can be posted.");

        Validate();
        Status = InvoiceStatus.Posted;
    }

    public void MarkPaid()
    {
        if (Status != InvoiceStatus.Posted)
            throw new InvalidOperationException("Only posted invoices can be marked as paid.");

        Status = InvoiceStatus.Paid;
    }
}

public enum InvoiceStatus
{
    Draft,
    Posted,
    Paid,
    Cancelled
}
