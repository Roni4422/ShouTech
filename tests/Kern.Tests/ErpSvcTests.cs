using System;
using System.Linq;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using Xunit;

namespace Kern.Tests;

public class ErpSvcTests
{
    [Fact]
    public void InventoryItem_ShouldRejectNegativeAdjustmentsBelowStock()
    {
        var svc = new ErpSvc();
        var item = svc.CreateItem("ITM-001", "Laptop", 1500m, 10m, "PCS", 3m);

        var ex = Assert.Throws<InvalidOperationException>(() => svc.AdjustStock(item.Id, -20m, Guid.NewGuid()));
        Assert.Contains("Insufficient stock", ex.Message);
    }

    [Fact]
    public void SalesInvoice_ShouldCalculateTaxAndTotalCorrectly()
    {
        var svc = new ErpSvc();
        svc.CreateCustomer("C-001", "Apex Shop");
        var item = svc.CreateItem("ITM-002", "Printer", 100m, 20m, "PCS", 5m);
        var customer = svc.Customers.Single();

        var invoice = svc.CreateSalesInvoice(customer.Id, "INV-1001", new DateOnly(2026, 10, 10), "SAR");
        svc.AddInvoiceLine(invoice.Id, item.Id, "Printer", 2m, 100m, 15m);

        Assert.Equal(200m, invoice.Subtotal);
        Assert.Equal(30m, invoice.TaxTotal);
        Assert.Equal(230m, invoice.Total);
    }

    [Fact]
    public void PostingSalesInvoice_ShouldReduceInventoryAndSetPostedStatus()
    {
        var svc = new ErpSvc();
        var customer = svc.CreateCustomer("C-002", "Blue Retail");
        var item = svc.CreateItem("ITM-003", "Keyboard", 80m, 12m, "PCS", 4m);
        var invoice = svc.CreateSalesInvoice(customer.Id, "INV-1002", new DateOnly(2026, 10, 11), "SAR");

        svc.AddInvoiceLine(invoice.Id, item.Id, "Keyboard", 3m, 80m, 0m);
        var posted = svc.PostInvoice(invoice.Id);

        Assert.Equal(9m, svc.Items.Single(x => x.Id == item.Id).StockOnHand);
        Assert.Equal(InvoiceStatus.Posted, posted.Status);
    }

    [Fact]
    public void ErpSvc_ShouldCreateVendorAndCustomerRecords()
    {
        var svc = new ErpSvc();

        var customer = svc.CreateCustomer("C-003", "North Market", "0500000000", "north@example.com");
        var vendor = svc.CreateVendor("V-003", "Paper Supply", "0555555555", "paper@example.com");

        Assert.Equal("SAR", customer.CurrencyCode);
        Assert.Equal("V-003", vendor.Code);
        Assert.Contains(svc.Vendors, x => x.Id == vendor.Id);
    }
}
