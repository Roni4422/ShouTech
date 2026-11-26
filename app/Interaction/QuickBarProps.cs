using System.Windows;
using System.Windows.Media;
using System;

namespace ShouTech.App.Interaction
{
// ---- merged from cod/app/src/Interaction/EntityContextAction.cs ----
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

    /// <summary>
    /// Attached properties that carry a quick-access bar button's icon.
    ///
    /// The central QuickAccessButtonStyle template (Themes/ShRibn.xaml) renders
    /// the <see cref="GlyphProperty"/> (a Segoe MDL2 Assets code point) until an
    /// <see cref="IconProperty"/> image is assigned. Assigning Icon — for example
    /// a PNG dropped into Resources/Ico — replaces the glyph with the image and
    /// requires no other change: the swap is a single attribute on the button.
    /// </summary>
    public static class QuickBarProps
    {
        /// <summary>Segoe MDL2 Assets glyph shown while no Icon is assigned.</summary>
        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.RegisterAttached(
                "Glyph",
                typeof(string),
                typeof(QuickBarProps),
                new PropertyMetadata(string.Empty));

        public static string GetGlyph(DependencyObject obj) =>
            (string)obj.GetValue(GlyphProperty);

        public static void SetGlyph(DependencyObject obj, string value) =>
            obj.SetValue(GlyphProperty, value);

        /// <summary>
        /// Optional icon image (e.g. "/Resources/Ico/QbInvoice.png"). When set,
        /// the quick-access template shows the image instead of the glyph.
        /// </summary>
        public static readonly DependencyProperty IconProperty =
            DependencyProperty.RegisterAttached(
                "Icon",
                typeof(ImageSource),
                typeof(QuickBarProps),
                new PropertyMetadata(null, OnIconChanged));

        public static ImageSource? GetIcon(DependencyObject obj) =>
            (ImageSource?)obj.GetValue(IconProperty);

        public static void SetIcon(DependencyObject obj, ImageSource? value) =>
            obj.SetValue(IconProperty, value);

        /// <summary>
        /// True while an Icon image is assigned. Maintained automatically by the
        /// Icon property-changed callback so the central template can switch
        /// between the glyph and the image with a plain DataTrigger.
        /// </summary>
        public static readonly DependencyProperty HasIconProperty =
            DependencyProperty.RegisterAttached(
                "HasIcon",
                typeof(bool),
                typeof(QuickBarProps),
                new PropertyMetadata(false));

        public static bool GetHasIcon(DependencyObject obj) =>
            (bool)obj.GetValue(HasIconProperty);

        public static void SetHasIcon(DependencyObject obj, bool value) =>
            obj.SetValue(HasIconProperty, value);

        private static void OnIconChanged(
            DependencyObject d,
            DependencyPropertyChangedEventArgs e) =>
            d.SetValue(HasIconProperty, e.NewValue != null);
    }
}
