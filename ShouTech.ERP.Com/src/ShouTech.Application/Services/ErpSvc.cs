using System;
using System.Collections.Generic;
using System.Linq;
using ShouTech.Domain.Entities;

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
