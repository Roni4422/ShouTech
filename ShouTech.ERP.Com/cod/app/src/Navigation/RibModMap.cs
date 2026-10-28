using System;
using System.Collections.Generic;

namespace ShouTech.App.Navigation
{
    /// <summary>
    /// Maps ribbon tab identifiers to ERP module categories for navigation context.
    /// </summary>
    public static class RibModMap
    {
        private static readonly Dictionary<string, string> TabToModule = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TabFile"] = "File",
            ["TabInventory"] = "Inventory",
            ["TabAccounts"] = "Accounting",
            ["TabBonds"] = "Vouchers",
            ["TabInvoices"] = "Invoices",
            ["TabPOS"] = "PointOfSale",
            ["TabOrders"] = "Orders",
            ["TabCustomers"] = "Customers",
            ["TabSalesRep"] = "SalesRepresentative",
            ["TabManufacturing"] = "Manufacturing",
            ["TabBanks"] = "Banks",
            ["TabReports"] = "Reports",
            ["TabTools"] = "Tools",
            ["TabOtherTools"] = "Administration",
            ["TabView"] = "View",
            ["TabExit"] = "Session",
            ["TabHelp"] = "Help"
        };

        private static readonly Dictionary<string, string> HeaderToModule = new(StringComparer.Ordinal)
        {
            ["📁 ملف"] = "File",
            ["📦 مستودعات"] = "Inventory",
            ["💰 حسابات"] = "Accounting",
            ["📋 سندات"] = "Vouchers",
            ["🧾 فواتير"] = "Invoices",
            ["🛍️ نقاط بيع"] = "PointOfSale",
            ["📋 طلبيات"] = "Orders",
            ["👤 عملاء"] = "Customers",
            ["🚶 مندوب"] = "SalesRepresentative",
            ["🏗️ تصنيع"] = "Manufacturing",
            ["🏦 بنوك"] = "Banks",
            ["📊 تقارير"] = "Reports",
            ["🔧 أدوات"] = "Tools",
            ["⚙️ أدوات أخرى"] = "Administration",
            ["👁️ عرض"] = "View",
            ["🚪 خروج"] = "Session",
            ["❓ مساعدة"] = "Help"
        };

        public static string ResolveModule(string? tabName, string? tabHeader)
        {
            if (!string.IsNullOrWhiteSpace(tabName) &&
                TabToModule.TryGetValue(tabName, out var fromName))
                return fromName;

            if (!string.IsNullOrWhiteSpace(tabHeader) &&
                HeaderToModule.TryGetValue(tabHeader, out var fromHeader))
                return fromHeader;

            return "General";
        }

        public static string GetModuleDisplayName(string moduleKey) => moduleKey switch
        {
            "File" => "ملف",
            "Inventory" => "مستودعات",
            "Accounting" => "حسابات",
            "Vouchers" => "سندات",
            "Invoices" => "فواتير",
            "PointOfSale" => "نقاط البيع",
            "Orders" => "طلبيات",
            "Customers" => "عملاء",
            "SalesRepresentative" => "مندوب مبيعات",
            "Manufacturing" => "تصنيع",
            "Banks" => "بنوك",
            "Reports" => "تقارير",
            "Tools" => "أدوات",
            "Administration" => "إدارة",
            "View" => "عرض",
            "Session" => "جلسة",
            "Help" => "مساعدة",
            _ => "عام"
        };
    }
}
