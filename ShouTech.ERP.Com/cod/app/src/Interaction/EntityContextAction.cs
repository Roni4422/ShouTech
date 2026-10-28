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
            new("item.details", "تفاصيل المادة", "Inventory", contextType),
            new("item.movements", "حركة المادة", "Inventory", contextType),
            new("item.sales", "مبيعات المادة", "Reports", contextType),
            new("item.valuation", "تقييم المخزون", "Reports", contextType)
        ],
        EntityContextType.Account =>
        [
            new("account.card", "كشف الحساب", "Accounting", contextType),
            new("account.ledger", "دفتر الأستاذ", "Accounting", contextType),
            new("account.balance", "ميزان المراجعة", "Reports", contextType),
            new("account.analysis", "تحليل الحساب", "Reports", contextType)
        ],
        _ => Array.Empty<EntityContextAction>()
    };
}