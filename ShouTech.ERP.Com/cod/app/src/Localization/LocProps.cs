using System.Windows;

namespace ShouTech.App.Localization
{
    /// <summary>
    /// Attached properties used by the centralized ShouTech localization engine.
    /// These properties only declare localization metadata.
    /// UiLoczr is responsible for applying the localized values.
    /// </summary>
    public static class LocProps
    {
        public static readonly DependencyProperty KeyProperty =
            DependencyProperty.RegisterAttached(
                "Key",
                typeof(string),
                typeof(LocProps),
                new PropertyMetadata(null));

        public static readonly DependencyProperty IconProperty =
            DependencyProperty.RegisterAttached(
                "Icon",
                typeof(string),
                typeof(LocProps),
                new PropertyMetadata(null));

        public static readonly DependencyProperty ToolTipKeyProperty =
            DependencyProperty.RegisterAttached(
                "ToolTipKey",
                typeof(string),
                typeof(LocProps),
                new PropertyMetadata(null));

        public static readonly DependencyProperty WindowKeyProperty =
            DependencyProperty.RegisterAttached(
                "WindowKey",
                typeof(string),
                typeof(LocProps),
                new PropertyMetadata(null));

        /// <summary>
        /// Optional localization key used for a DataGrid column header.
        /// Kept separate so column localization can be handled explicitly.
        /// </summary>
        public static readonly DependencyProperty HeaderKeyProperty =
            DependencyProperty.RegisterAttached(
                "HeaderKey",
                typeof(string),
                typeof(LocProps),
                new PropertyMetadata(null));

        /// <summary>
        /// Optional localization key for ComboBox/Selector items.
        /// </summary>
        public static readonly DependencyProperty ItemKeyProperty =
            DependencyProperty.RegisterAttached(
                "ItemKey",
                typeof(string),
                typeof(LocProps),
                new PropertyMetadata(null));

        public static void SetKey(
            DependencyObject element,
            string? value) =>
            element.SetValue(KeyProperty, value);

        public static string? GetKey(
            DependencyObject element) =>
            (string?)element.GetValue(KeyProperty);

        public static void SetIcon(
            DependencyObject element,
            string? value) =>
            element.SetValue(IconProperty, value);

        public static string? GetIcon(
            DependencyObject element) =>
            (string?)element.GetValue(IconProperty);

        public static void SetToolTipKey(
            DependencyObject element,
            string? value) =>
            element.SetValue(ToolTipKeyProperty, value);

        public static string? GetToolTipKey(
            DependencyObject element) =>
            (string?)element.GetValue(ToolTipKeyProperty);

        public static void SetWindowKey(
            DependencyObject element,
            string? value) =>
            element.SetValue(WindowKeyProperty, value);

        public static string? GetWindowKey(
            DependencyObject element) =>
            (string?)element.GetValue(WindowKeyProperty);

        public static void SetHeaderKey(
            DependencyObject element,
            string? value) =>
            element.SetValue(HeaderKeyProperty, value);

        public static string? GetHeaderKey(
            DependencyObject element) =>
            (string?)element.GetValue(HeaderKeyProperty);

        public static void SetItemKey(
            DependencyObject element,
            string? value) =>
            element.SetValue(ItemKeyProperty, value);

        public static string? GetItemKey(
            DependencyObject element) =>
            (string?)element.GetValue(ItemKeyProperty);
    }
}