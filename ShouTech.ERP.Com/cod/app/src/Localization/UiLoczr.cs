using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Controls.Ribbon;
using ShouTech.App.Services;
using WpfApp = System.Windows.Application;

namespace ShouTech.App.Localization
{
    /// <summary>
    /// Centralized UI localization engine for ShouTech ERP.
    ///
    /// LocProps declares localization metadata.
    /// UiLoczr applies localized values to the WPF visual/logical tree.
    ///
    /// Important:
    /// TextBox.Text, PasswordBox.Password and user-entered values are never
    /// overwritten by this service.
    /// </summary>
    public static class UiLoczr
    {
        private static bool _initialized;
        private static bool _isApplying;

        private static readonly Dictionary<string, (string Key, string Icon)> RibbonTabKeys =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["TabFile"] = ("Menu.File", "📁"),
                ["TabInventory"] = ("Menu.Inventory", "📦"),
                ["TabAccounts"] = ("Menu.Accounts", "💰"),
                ["TabBonds"] = ("Menu.Bonds", "📋"),
                ["TabInvoices"] = ("Menu.Invoices", "🧾"),
                ["TabPOS"] = ("Menu.POS", "🛍️"),
                ["TabOrders"] = ("Menu.Orders", "📋"),
                ["TabCustomers"] = ("Menu.Customers", "👤"),
                ["TabSalesRep"] = ("Menu.SalesRep", "🚶"),
                ["TabManufacturing"] = ("Menu.Manufacturing", "🏗️"),
                ["TabBanks"] = ("Menu.Banks", "🏦"),
                ["TabReports"] = ("Menu.Reports", "📊"),
                ["TabTools"] = ("Menu.Tools", "🔧"),
                ["TabOtherTools"] = ("Menu.OtherTools", "⚙️"),
                ["TabView"] = ("Menu.View", "👁️"),
                ["TabExit"] = ("Menu.Exit", "🚪"),
                ["TabHelp"] = ("Menu.Help", "❓")
            };

        public static void Initialize(ILocSvc localization)
        {
            if (_initialized)
                return;

            ArgumentNullException.ThrowIfNull(localization);

            localization.LanguageChanged += (_, _) =>
                ScheduleApplyToAllWindows();

            _initialized = true;
        }

        public static void ScheduleApplyToAllWindows()
        {
            if (WpfApp.Current?.Dispatcher == null)
            {
                ApplyToAllWindows();
                return;
            }

            WpfApp.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(ApplyToAllWindows));
        }

        public static void ApplyToAllWindows()
        {
            if (WpfApp.Current == null)
                return;

            if (_isApplying)
                return;

            try
            {
                _isApplying = true;

                foreach (Window window in WpfApp.Current.Windows)
                    ApplyToWindow(window);
            }
            finally
            {
                _isApplying = false;
            }
        }

        public static void ApplyToWindow(Window window)
        {
            if (window == null)
                return;

            ApplyToRoot(window);
        }

        public static void ApplyToRoot(DependencyObject root)
        {
            if (root == null)
                return;

            var localization = ResolveLocalization();

            if (localization == null)
                return;

            if (root is Window window)
            {
                var windowKey = LocProps.GetWindowKey(window);

                if (!string.IsNullOrWhiteSpace(windowKey))
                {
                    window.Title =
                        localization.GetString(
                            windowKey,
                            window.Title ?? windowKey);
                }
            }

            WalkTree(root, localization);
        }

        private static void WalkTree(
            DependencyObject parent,
            ILocSvc localization)
        {
            if (parent == null)
                return;

            ApplyElement(parent, localization);

            /*
             * DataGrid columns are not logical-tree children.
             * They therefore require explicit handling.
             */
            if (parent is DataGrid dataGrid)
                ApplyDataGrid(dataGrid, localization);

            foreach (var child in GetLogicalChildren(parent))
                WalkTree(child, localization);
        }

        private static IEnumerable<DependencyObject> GetLogicalChildren(
            DependencyObject parent)
        {
            if (parent is null)
                yield break;

            /*
             * Ribbon has a special item structure.
             */
            if (parent is Ribbon ribbon)
            {
                foreach (var item in ribbon.Items)
                {
                    if (item is DependencyObject ribbonItem)
                        yield return ribbonItem;
                }

                yield break;
            }

            /*
             * DataGrid columns are deliberately excluded from the logical
             * tree here because ApplyDataGrid handles them explicitly.
             */
            if (parent is DataGrid)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(parent))
                {
                    if (child is DependencyObject dependencyObject)
                        yield return dependencyObject;
                }

                yield break;
            }

            foreach (var child in LogicalTreeHelper.GetChildren(parent))
            {
                if (child is DependencyObject dependencyObject)
                    yield return dependencyObject;
            }
        }

        private static void ApplyElement(
            DependencyObject element,
            ILocSvc localization)
        {
            /*
             * ToolTip localization is generic and therefore must happen
             * before the control-specific cases.
             */
            ApplyToolTip(element, localization);

            switch (element)
            {
                case RibbonTab tab
                    when tab.Name is { Length: > 0 } name &&
                         RibbonTabKeys.TryGetValue(
                             name,
                             out var tabInfo):

                    tab.Header =
                        Format(
                            localization,
                            tabInfo.Key,
                            tabInfo.Icon);

                    break;

                case RibbonGroup group:

                    ApplyRibbonGroup(
                        group,
                        localization);

                    break;

                case RibbonButton ribbonButton:

                    ApplyRibbonButton(
                        ribbonButton,
                        localization);

                    break;

                case MenuItem menuItem:

                    ApplyMenuItem(
                        menuItem,
                        localization);

                    break;

                case TabItem tabItem:

                    ApplyHeaderedElement(
                        tabItem,
                        localization);

                    break;

                case GroupBox groupBox:

                    ApplyHeaderedElement(
                        groupBox,
                        localization);

                    break;

                case Label label:

                    ApplyLabel(
                        label,
                        localization);

                    break;

                case TextBlock textBlock:

                    ApplyTextBlock(
                        textBlock,
                        localization);

                    break;

                case CheckBox checkBox:

                    ApplyToggleButton(
                        checkBox,
                        localization);

                    break;

                case RadioButton radioButton:

                    ApplyToggleButton(
                        radioButton,
                        localization);

                    break;

                case ToggleButton toggleButton:

                    ApplyToggleButton(
                        toggleButton,
                        localization);

                    break;

                case ComboBox comboBox:

                    ApplyComboBox(
                        comboBox,
                        localization);

                    break;

                case ComboBoxItem comboBoxItem:

                    ApplyComboBoxItem(
                        comboBoxItem,
                        localization);

                    break;

                case Button button:

                    ApplyButton(
                        button,
                        localization);

                    break;

                /*
                 * Intentionally do not translate TextBox.Text.
                 * It may contain user data.
                 */
                case TextBox:

                    break;

                /*
                 * Intentionally do not touch PasswordBox.Password.
                 */
                case PasswordBox:

                    break;
            }
        }

        private static void ApplyToolTip(
            DependencyObject element,
            ILocSvc localization)
        {
            var toolTipKey = LocProps.GetToolTipKey(element);

            if (string.IsNullOrWhiteSpace(toolTipKey))
                return;

            if (element is FrameworkElement frameworkElement)
            {
                var fallback =
                    frameworkElement.ToolTip?.ToString()
                    ?? toolTipKey;

                frameworkElement.ToolTip =
                    localization.GetString(
                        toolTipKey,
                        fallback);
            }
        }

        private static void ApplyRibbonGroup(
            RibbonGroup group,
            ILocSvc localization)
        {
            var key =
                ResolveElementKey(
                    group,
                    localization,
                    group.Header?.ToString());

            if (string.IsNullOrWhiteSpace(key))
                return;

            group.Tag = key;

            group.Header =
                localization.GetString(
                    key,
                    group.Header?.ToString() ?? key);
        }

        private static void ApplyRibbonButton(
            RibbonButton button,
            ILocSvc localization)
        {
            var key =
                ResolveElementKey(
                    button,
                    localization,
                    button.Label);

            if (string.IsNullOrWhiteSpace(key))
                return;

            button.Tag = key;

            button.Label =
                localization.GetString(
                    key,
                    button.Label ?? key);
        }

        private static void ApplyMenuItem(
            MenuItem menuItem,
            ILocSvc localization)
        {
            var key = LocProps.GetKey(menuItem);

            if (string.IsNullOrWhiteSpace(key))
                return;

            menuItem.Header =
                Format(
                    localization,
                    key,
                    LocProps.GetIcon(menuItem));
        }

        private static void ApplyTextBlock(
            TextBlock textBlock,
            ILocSvc localization)
        {
            var key = LocProps.GetKey(textBlock);

            if (string.IsNullOrWhiteSpace(key))
                return;

            textBlock.Text =
                localization.GetString(
                    key,
                    textBlock.Text ?? key);
        }

        private static void ApplyLabel(
            Label label,
            ILocSvc localization)
        {
            var key = LocProps.GetKey(label);

            if (string.IsNullOrWhiteSpace(key))
                return;

            label.Content =
                localization.GetString(
                    key,
                    label.Content?.ToString() ?? key);
        }

        private static void ApplyButton(
            Button button,
            ILocSvc localization)
        {
            var key = LocProps.GetKey(button);

            if (string.IsNullOrWhiteSpace(key))
                return;

            button.Content =
                localization.GetString(
                    key,
                    button.Content?.ToString() ?? key);
        }

        private static void ApplyToggleButton(
            ToggleButton button,
            ILocSvc localization)
        {
            var key = LocProps.GetKey(button);

            if (string.IsNullOrWhiteSpace(key))
                return;

            button.Content =
                localization.GetString(
                    key,
                    button.Content?.ToString() ?? key);
        }

        private static void ApplyHeaderedElement(
            HeaderedContentControl element,
            ILocSvc localization)
        {
            var key = LocProps.GetKey(element);

            if (string.IsNullOrWhiteSpace(key))
                return;

            element.Header =
                localization.GetString(
                    key,
                    element.Header?.ToString() ?? key);
        }

        private static void ApplyComboBox(
            ComboBox comboBox,
            ILocSvc localization)
        {
            /*
             * ComboBox itself normally has no visible caption.
             * Its items are handled independently.
             */
            foreach (var item in comboBox.Items)
            {
                if (item is DependencyObject dependencyObject)
                    ApplyElement(
                        dependencyObject,
                        localization);
            }
        }

        private static void ApplyComboBoxItem(
            ComboBoxItem item,
            ILocSvc localization)
        {
            var key = LocProps.GetKey(item);

            if (string.IsNullOrWhiteSpace(key))
                key = LocProps.GetItemKey(item);

            if (string.IsNullOrWhiteSpace(key))
                return;

            item.Content =
                localization.GetString(
                    key,
                    item.Content?.ToString() ?? key);
        }

        private static void ApplyDataGrid(
            DataGrid dataGrid,
            ILocSvc localization)
        {
            foreach (var column in dataGrid.Columns)
                ApplyDataGridColumn(
                    column,
                    localization);
        }

        private static void ApplyDataGridColumn(
            DataGridColumn column,
            ILocSvc localization)
        {
            var key = LocProps.GetHeaderKey(column);

            if (string.IsNullOrWhiteSpace(key))
                key = LocProps.GetKey(column);

            if (string.IsNullOrWhiteSpace(key))
                return;

            var fallback =
                column.Header?.ToString()
                ?? key;

            column.Header =
                localization.GetString(
                    key,
                    fallback);
        }

        private static string? ResolveElementKey(
            FrameworkElement element,
            ILocSvc localization,
            string? currentText)
        {
            if (element.Tag is string tagKey &&
                !string.IsNullOrWhiteSpace(tagKey))
            {
                return tagKey;
            }

            var attachedKey =
                LocProps.GetKey(element);

            if (!string.IsNullOrWhiteSpace(attachedKey))
                return attachedKey;

            if (localization is LocSvc svc &&
                !string.IsNullOrWhiteSpace(currentText))
            {
                if (svc.TryResolveKeyFromLabel(
                        currentText,
                        out var resolved))
                {
                    return resolved;
                }
            }

            return null;
        }

        private static string Format(
            ILocSvc localization,
            string key,
            string? icon)
        {
            var text =
                localization.GetString(
                    key,
                    key);

            return string.IsNullOrWhiteSpace(icon)
                ? text
                : $"{icon} {text}";
        }

        private static ILocSvc? ResolveLocalization()
        {
            if (WpfApp.Current is App app)
                return app.Localization;

            return AppHost.Localization;
        }
    }
}