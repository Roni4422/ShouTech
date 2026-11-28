using System;
using System.Collections.Generic;
using ShouTech.App.Localization;
using ShouTech.App.Services;

namespace ShouTech.App.Navigation
{
    /// <summary>
    /// Maps ribbon tab identifiers to ERP module categories for navigation context.
    /// Display text is never matched or produced in a hard-coded language: tab
    /// headers are resolved through the locale-neutral label index built from
    /// the language catalogs (LocSvc.TryResolveKeyFromLabel), and module display
    /// names come from the catalogs via the Menu.* keys.
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
            ["TabHR"] = "HumanResources",
            ["TabBanks"] = "Banks",
            ["TabReports"] = "Reports",
            ["TabTools"] = "Tools",
            ["TabOtherTools"] = "Administration",
            ["TabView"] = "View",
            ["TabExit"] = "Session",
            ["TabHelp"] = "Help"
        };

        // Catalog key (as indexed from every language file) -> module key.
        private static readonly Dictionary<string, string> MenuKeyToModule = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Menu.File"] = "File",
            ["Menu.Inventory"] = "Inventory",
            ["Menu.Accounts"] = "Accounting",
            ["Menu.Bonds"] = "Vouchers",
            ["Menu.Invoices"] = "Invoices",
            ["Menu.POS"] = "PointOfSale",
            ["Menu.Orders"] = "Orders",
            ["Menu.Customers"] = "Customers",
            ["Menu.SalesRep"] = "SalesRepresentative",
            ["Menu.Manufacturing"] = "Manufacturing",
            ["Menu.HR"] = "HumanResources",
            ["Menu.Banks"] = "Banks",
            ["Menu.Reports"] = "Reports",
            ["Menu.Tools"] = "Tools",
            ["Menu.OtherTools"] = "Administration",
            ["Menu.View"] = "View",
            ["Menu.Exit"] = "Session",
            ["Menu.Help"] = "Help"
        };

        // Module key -> catalog key used for its localized display name.
        private static readonly Dictionary<string, string> ModuleToMenuKey = new(StringComparer.OrdinalIgnoreCase)
        {
            ["File"] = "Menu.File",
            ["Inventory"] = "Menu.Inventory",
            ["Accounting"] = "Menu.Accounts",
            ["Vouchers"] = "Menu.Bonds",
            ["Invoices"] = "Menu.Invoices",
            ["PointOfSale"] = "Menu.POS",
            ["Orders"] = "Menu.Orders",
            ["Customers"] = "Menu.Customers",
            ["SalesRepresentative"] = "Menu.SalesRep",
            ["Manufacturing"] = "Menu.Manufacturing",
            ["HumanResources"] = "Menu.HR",
            ["Banks"] = "Menu.Banks",
            ["Reports"] = "Menu.Reports",
            ["Tools"] = "Menu.Tools",
            ["Administration"] = "Menu.OtherTools",
            ["View"] = "Menu.View",
            ["Session"] = "Menu.Exit",
            ["Help"] = "Menu.Help"
        };

        public static string ResolveModule(string? tabName, string? tabHeader)
        {
            if (!string.IsNullOrWhiteSpace(tabName) &&
                TabToModule.TryGetValue(tabName, out var fromName))
                return fromName;

            // The header is display text in whichever language the UI is
            // currently using; resolve it to its catalog key first, then map
            // the key (never the localized text) to a module.
            if (!string.IsNullOrWhiteSpace(tabHeader) &&
                AppHost.Localization is LocSvc locSvc &&
                locSvc.TryResolveKeyFromLabel(tabHeader, out var catalogKey) &&
                MenuKeyToModule.TryGetValue(catalogKey, out var fromHeader))
                return fromHeader;

            return "General";
        }

        public static string GetModuleDisplayName(string moduleKey)
        {
            if (!string.IsNullOrWhiteSpace(moduleKey) &&
                ModuleToMenuKey.TryGetValue(moduleKey, out var catalogKey))
                return Loc.Get(catalogKey, moduleKey);

            return Loc.Get("Menu.General", "General");
        }
    }
}
