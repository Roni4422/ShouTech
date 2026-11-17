using System;

namespace ShouTech.App.Interaction;

public enum EntityContextType
{
    Item,
    Account
}

public sealed record EntityContextAction(
    string Key,
    string Label,
    string ModuleKey,
    EntityContextType ContextType);

public static class EntityContextActions
{
    public static IReadOnlyList<EntityContextAction> For(EntityContextType contextType) => contextType switch
    {
        EntityContextType.Item =>
        [
            new("item.details", "Item Details", "Inventory", contextType),
            new("item.movements", "Item Movements", "Inventory", contextType),
            new("item.sales", "Item Sales", "Reports", contextType),
            new("item.valuation", "Inventory Valuation", "Reports", contextType)
        ],
        EntityContextType.Account =>
        [
            new("account.card", "Account Statement", "Accounting", contextType),
            new("account.ledger", "Ledger", "Accounting", contextType),
            new("account.balance", "Trial Balance", "Reports", contextType),
            new("account.analysis", "Account Analysis", "Reports", contextType)
        ],
        _ => Array.Empty<EntityContextAction>()
    };
}