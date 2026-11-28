using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Ribbon;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ShouTech.App.Configuration;
using ShouTech.App.Localization;
using ShouTech.App.Services;
using ShouTech.App.ViewModels;
using WpfApp = System.Windows.Application;

using ShouTech.Domain;
namespace ShouTech.App.Views
{
    public partial class MWin : RibbonWindow
    {
        private string _currentTab = string.Empty;

        private CoaWin? _coaWindow;
        private DatWin? _datWindow;
        private BkpWin? _bkpWindow;

        private bool _isClosing;

        public MWin()
        {
            DataContext = new MainShVM(this);

            try
            {
                InitializeComponent();
                PreviewKeyDown += OnHelpPreviewKeyDown;
                VisuId.ApplyWindow(this);
                Win11Chrome.Apply(this);
                ApplyUiDirection();
                Loaded += (_, _) => InitializeRecycleBinPosition();
                _ = RefreshRecycleBinBadgeAsync();

                SourceInitialized += OnSourceInitialized;

                ApplyWindowPreferences();

                if (DataContext is MainShVM viewModel)
                {
                    viewModel.DockedWindows.CollectionChanged +=
                        OnDockedWindowsChanged;

                    viewModel.RequestOpenMaterialCard +=
                        OnRequestOpenMaterialCard;

                    viewModel.RequestOpenMaterialGroups +=
                        OnRequestOpenMaterialGroups;
                }

                SizeChanged += OnMainWindowSizeChanged;

                // The main shell always opens maximized.
                WindowState = WindowState.Maximized;

                ApplyUiDirection();
                UpdateWindowChromeState();
                RefreshLocalizedText();
                BuildNavShell();

                // Apply language direction safely.
                try
                {
                    LangDirSv.ApplyToWindow(this);
                }
                catch (Exception exL73)
                {
                    AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, exL73, "MWin.xaml.MWin");
                    // Non-fatal.
                }

                // Apply localization keys to ribbon/menu.
                try
                {
                    UiLoczr.ApplyToWindow(this);
                }
                catch (Exception exL83)
                {
                    AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, exL83, "MWin.xaml.MWin");
                    // Non-fatal.
                }

                AppServiceHost.Localization.LanguageChanged +=
                    OnLanguageChanged;

                StateChanged += OnWindowStateChanged;

                Closed += (_, _) =>
                {
                    AppServiceHost.Localization.LanguageChanged -=
                        OnLanguageChanged;

                    if (DataContext is MainShVM vm)
                    {
                        vm.DockedWindows.CollectionChanged -=
                            OnDockedWindowsChanged;

                        vm.RequestOpenMaterialCard -=
                            OnRequestOpenMaterialCard;

                        vm.RequestOpenMaterialGroups -=
                            OnRequestOpenMaterialGroups;

                        vm.Dispose();
                    }
                };
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, ex, "MWin.xaml.MWin");
                MessageBox.Show(
                    $"Error: {ex.Message}",
                    "ShouTech ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void OnHelpPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F1 || e.IsRepeat)
                return;

            e.Handled = true;
            try
            {
                var help = new HelpWin { Owner = this };
                help.ShowDialog();
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MWin.F1Help");
            }
        }

        private void OnWindowStateChanged(object? sender, EventArgs e)
		{
			if (RootBorder == null)
				return;

			RootBorder.CornerRadius =
				WindowState == WindowState.Maximized
					? new CornerRadius(0)
					: new CornerRadius(14);

			UpdateWindowChromeState();
		}

        private void OnAccentPickerClick(
            object sender,
            RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = AccentPickerButton,
                Placement =
                    System.Windows.Controls.Primitives.PlacementMode.Bottom
            };

            var visual = AppHost.Visual;

            foreach (var accent in VisuId.AvailableAccents)
            {
                var colorBrush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(
                        accent.Primary));

                colorBrush.Freeze();

                var iconBorder = new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(3),
                    BorderBrush =
                        new SolidColorBrush(
                            Color.FromRgb(
                                0x55,
                                0x55,
                                0x55)),
                    BorderThickness = new Thickness(1),
                    Background = colorBrush
                };

                var item = new MenuItem
                {
                    Header = accent.Display,
                    Icon = iconBorder,
                    Tag = accent.Key
                };

                // Mark the current accent.
                if (string.Equals(
                        accent.Key,
                        visual.GetCurrentAccent(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    item.FontWeight = FontWeights.Bold;
                }

                item.Click += (s, _) =>
                {
                    visual.SetAccent(accent.Key);
                    menu.IsOpen = false;
                };

                menu.Items.Add(item);
            }

            menu.IsOpen = true;
        }

        private void OnMainWindowSizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            if (DataContext is MainShVM viewModel)
            {
                viewModel.LayoutDockedWindows(
                    InternalWindowDock.ActualWidth);
            }
        }

        private void OnDockedWindowsChanged(
            object? sender,
            NotifyCollectionChangedEventArgs e)
        {
            if (DataContext is not MainShVM viewModel)
                return;

            InternalWindowDock.Visibility =
                viewModel.DockedWindows.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            Dispatcher.BeginInvoke(
                new Action(() =>
                    viewModel.LayoutDockedWindows(
                        InternalWindowDock.ActualWidth)));
        }

        private void OnLanguageChanged(
            object? sender,
            EventArgs e)
        {
            try
            {
                LangDirSv.ApplyToWindow(this);
            }
            catch (Exception exL237)
            {
                AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, exL237, "MWin.xaml.OnLanguageChanged");
                // Non-fatal.
            }

            ApplyUiDirection();
            RefreshLocalizedText();

            try
            {
                UiLoczr.ApplyToWindow(this);
            }
            catch (Exception exL249)
            {
                AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, exL249, "MWin.xaml.OnLanguageChanged");
                // Non-fatal.
            }
        }

        private void ApplyUiDirection()
        {
            var language = AppHost.Localization.GetCurrentLanguage();
            var direction =
                LanguageDirectionService.GetFlowDirection(language);
            FlowDirection = direction;

            if (MainMenu != null)
                MainMenu.FlowDirection = direction;

            if (QuickAccessBar != null)
                QuickAccessBar.FlowDirection = direction;

            if (RibbonControl != null)
                RibbonControl.FlowDirection = direction;

            if (NavPane != null)
                NavPane.FlowDirection = direction;

            if (CommandBar != null)
                CommandBar.FlowDirection = direction;

            if (MainStatusBar != null)
                MainStatusBar.FlowDirection = direction;

        }

        private void RecycleBinLayer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateRecycleBinVisibility();
        }

        private void UpdateRecycleBinVisibility()
        {
            if (RecycleBinLayer is null || RecycleBinCornerButton is null)
                return;

            const double minimumWidth = 120;
            const double minimumHeight = 146;
            var width = RecycleBinLayer.ActualWidth;
            var height = RecycleBinLayer.ActualHeight;
            var buttonWidth = RecycleBinCornerButton.ActualWidth > 0
                ? RecycleBinCornerButton.ActualWidth
                : RecycleBinCornerButton.Width;
            var buttonHeight = RecycleBinCornerButton.ActualHeight > 0
                ? RecycleBinCornerButton.ActualHeight
                : RecycleBinCornerButton.Height;
            var hasRoom = width >= Math.Max(minimumWidth, buttonWidth) &&
                          height >= Math.Max(minimumHeight, buttonHeight);

            if (!hasRoom)
            {
                RecycleBinCornerButton.Visibility = Visibility.Collapsed;
                return;
            }

            var left = Canvas.GetLeft(RecycleBinCornerButton);
            var top = Canvas.GetTop(RecycleBinCornerButton);
            if (!double.IsNaN(left) && !double.IsNaN(top))
            {
                var adjustedLeft = Math.Clamp(left, 0, width - buttonWidth);
                var adjustedTop = Math.Clamp(top, 0, height - buttonHeight);
                if (Math.Abs(adjustedLeft - left) > 0.5 ||
                    Math.Abs(adjustedTop - top) > 0.5)
                {
                    Canvas.SetLeft(RecycleBinCornerButton, adjustedLeft);
                    Canvas.SetTop(RecycleBinCornerButton, adjustedTop);
                    var preferences = UsrPref.Load();
                    preferences.RecycleBinLeft = adjustedLeft;
                    preferences.RecycleBinTop = adjustedTop;
                    preferences.Save();
                }
            }

            RecycleBinCornerButton.Visibility = Visibility.Visible;
        }

        private static bool IsArabicUi()
        {
            var language = AppHost.Localization.GetCurrentLanguage()
                ?? App.CurrentLanguageCode
                ?? string.Empty;
            if (LanguageDirectionService.IsRtl(language))
                return true;
            if (language.StartsWith("ar", StringComparison.OrdinalIgnoreCase))
                return true;
            var appLang = App.CurrentLanguageCode ?? string.Empty;
            return appLang.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
        }

        private void InitializeRecycleBinPosition()
        {
            if (RecycleBinLayer is null || RecycleBinCornerButton is null)
                return;

            var preferences = UsrPref.Load();
            var width = RecycleBinLayer.ActualWidth;
            var height = RecycleBinLayer.ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            var left = preferences.RecycleBinLeft;
            var top = preferences.RecycleBinTop;
            if (left < 0 || top < 0)
            {
                left = IsArabicUi() ? 24 : Math.Max(24, width - RecycleBinCornerButton.Width - 24);
                top = Math.Max(8, height - RecycleBinCornerButton.Height - 8);
            }

            Canvas.SetLeft(RecycleBinCornerButton, left);
            Canvas.SetTop(RecycleBinCornerButton, top);
            RecycleBinCornerButton.FlowDirection = FlowDirection.LeftToRight;
            UpdateRecycleBinVisibility();
        }

        private void RecycleBin_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (RecycleBinCornerButton is null || RecycleBinLayer is null)
                return;

            _suppressRecycleBinClick = false;
            _recycleBinDragOrigin = e.GetPosition(RecycleBinLayer);
            _recycleBinStartLeft = Canvas.GetLeft(RecycleBinCornerButton);
            _recycleBinStartTop = Canvas.GetTop(RecycleBinCornerButton);
            _isDraggingRecycleBin = false;
            // ButtonBase owns mouse capture so a normal click still raises Click.
        }

        private void RecycleBin_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed ||
                RecycleBinCornerButton is null || RecycleBinLayer is null ||
                !RecycleBinCornerButton.IsMouseCaptured)
                return;

            var current = e.GetPosition(RecycleBinLayer);
            var dx = current.X - _recycleBinDragOrigin.X;
            var dy = current.Y - _recycleBinDragOrigin.Y;
            if (!_isDraggingRecycleBin &&
                Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _isDraggingRecycleBin = true;
            var maxLeft = Math.Max(0, RecycleBinLayer.ActualWidth - RecycleBinCornerButton.ActualWidth);
            var maxTop = Math.Max(0, RecycleBinLayer.ActualHeight - RecycleBinCornerButton.ActualHeight);
            Canvas.SetLeft(RecycleBinCornerButton, Math.Clamp(_recycleBinStartLeft + dx, 0, maxLeft));
            Canvas.SetTop(RecycleBinCornerButton, Math.Clamp(_recycleBinStartTop + dy, 0, maxTop));
        }

        private void RecycleBin_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (RecycleBinCornerButton is null || !RecycleBinCornerButton.IsMouseCaptured)
                return;

            if (!_isDraggingRecycleBin)
                return;

            _suppressRecycleBinClick = true;
            var preferences = UsrPref.Load();
            preferences.RecycleBinLeft = Canvas.GetLeft(RecycleBinCornerButton);
            preferences.RecycleBinTop = Canvas.GetTop(RecycleBinCornerButton);
            preferences.Save();
            _isDraggingRecycleBin = false;

            // Keep capture intact until ButtonBase processes MouseLeftButtonUp.
        }

        // Quick Access Bar: each navigation button carries the same action key
        // the ribbon uses in its Tag, and is routed through the exact chain the
        // ribbon click uses (RibbonReportRouter -> RibbonNav -> RptNav ->
        // MainShVM.HandleRibbonAction), so a quick-bar button always opens the
        // same window as its ribbon counterpart.
        private void OnQuickAccessClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            var actionKey = button.Tag as string;

            if (string.IsNullOrWhiteSpace(actionKey))
                return;

            CollapseRibbon();

            try
            {
                if (RibbonReportRouter.TryHandle(actionKey, this))
                    return;

                if (RibbonNav.TryHandle(actionKey, this))
                    return;

                if (RptNav.TryHandle(actionKey, this))
                    return;

                if (DataContext is MainShVM viewModel)
                {
                    viewModel.HandleRibbonAction(
                        button.ToolTip?.ToString(),
                        actionKey,
                        null,
                        null);
                }
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(
                    ErrorCodes.NavigationFailed,
                    ex,
                    "MWin.xaml.OnQuickAccessClick");
            }
        }

        // Quick Access Bar Reports button: opens the ribbon on the Reports tab,
        // exactly like clicking the Reports menu.
        private void OnQuickReportsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                ShowRibbonTab("TabReports");
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(
                    ErrorCodes.ShellOperationFailed,
                    ex,
                    "MWin.xaml.OnQuickReportsClick");
            }
        }

        private bool _recycleBinOpen;
        private bool _isDraggingRecycleBin;
        private bool _suppressRecycleBinClick;
        private Point _recycleBinDragOrigin;
        private double _recycleBinStartLeft;
        private double _recycleBinStartTop;

        private async void OnRecycleBinClick(object sender, RoutedEventArgs e)
        {
            if (_suppressRecycleBinClick)
            {
                _suppressRecycleBinClick = false;
                e.Handled = true;
                return;
            }

            if (_recycleBinOpen)
                return;

            _recycleBinOpen = true;
            try
            {
                var bin = new BinWin { Owner = this };
                bin.ShowDialog();
                await RefreshRecycleBinBadgeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, ex, "MWin.xaml.OnRecycleBinClick");
                MessageBox.Show(
                    this,
                    ex.Message,
                    "Recycle Bin",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _recycleBinOpen = false;
            }
        }

        private async Task RefreshRecycleBinBadgeAsync()
        {
            try
            {
                var company = AppState.CurrentCompanyCode ?? string.Empty;
                var count = await RecycleBinSvc.CountAsync(company).ConfigureAwait(true);

                var tooltip = count > 0
                    ? string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        "Recycle bin — {0} item(s). Click to restore or permanently delete.",
                        count)
                    : "Recycle bin — empty. Soft-deleted master data appears here.";

                if (RecycleBinCornerButton != null)
                    RecycleBinCornerButton.ToolTip = tooltip;

            }
            catch (Exception exL395)
            {
                AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, exL395, "MWin.xaml.RefreshRecycleBinBadgeAsync");
                // Badge refresh must never interrupt the shell.
            }
        }

        private void OnTitleBarMouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (e.OriginalSource is DependencyObject source &&
                FindAncestor<Button>(source) is not null)
            {
                return;
            }

            var pointer = e.GetPosition(this);

            if (e.ClickCount == 2)
            {
                ToggleWindowState();
                return;
            }

            // Match native Windows behavior:
            // dragging the title area of a maximized window
            // restores it and keeps the pointer attached.
            if (WindowState == WindowState.Maximized)
            {
                var screenPoint = PointToScreen(pointer);
                var restore = RestoreBounds;

                var restoreWidth =
                    restore.Width > 0
                        ? restore.Width
                        : 1200;

                var restoreHeight =
                    restore.Height > 0
                        ? restore.Height
                        : 700;

                var ratioX =
                    ActualWidth > 0
                        ? pointer.X / ActualWidth
                        : 0.5;

                var grabY =
                    Math.Max(
                        0,
                        Math.Min(pointer.Y, 34));

                WindowState = WindowState.Normal;

                Width = restoreWidth;
                Height = restoreHeight;

                Left =
                    screenPoint.X -
                    (restoreWidth * ratioX);

                Top =
                    screenPoint.Y -
                    grabY;
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException exL468)
            {
                AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, exL468, "MWin.xaml.OnTitleBarMouseLeftButtonDown");
                // DragMove can fail during shutdown
                // or a state transition.
            }
        }

        private static T? FindAncestor<T>(
            DependencyObject? element)
            where T : DependencyObject
        {
            var current = element;

            while (current is not null)
            {
                if (current is T match)
                    return match;

                current =
                    VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void OnMinimizeClick(
            object sender,
            RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnMaximizeClick(
            object sender,
            RoutedEventArgs e)
        {
            ToggleWindowState();
        }

        private void OnCloseClick(
            object sender,
            RoutedEventArgs e)
        {
            Close();
        }

        private void ToggleWindowState()
        {
            WindowState =
                WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;

            UpdateWindowChromeState();
        }

        private void UpdateWindowChromeState()
        {
            if (MaximizeButton == null)
                return;

            var maximized =
                WindowState == WindowState.Maximized;

            MaximizeButton.Content =
                maximized
                    ? "❐"
                    : "□";

            MaximizeButton.ToolTip =
                maximized
                    ? "Restore"
                    : "Maximize";
        }

        private void RefreshLocalizedText()
        {
            string L(
                string key,
                string fallback) =>
                AppServiceHost.Localization.GetString(
                    $"Menu.{key}",
                    fallback);

            if (MenuFile != null)
            {
                MenuFile.Header =
                    L("File", "File");
            }

            if (MenuInventory != null)
            {
                MenuInventory.Header =
                    L("Inventory", "Inventory");
            }

            if (MenuAccounts != null)
            {
                MenuAccounts.Header =
                    L("Accounts", "Accounts");
            }

            if (MenuBonds != null)
            {
                MenuBonds.Header =
                    L("Bonds", "Vouchers");
            }

            if (MenuInvoices != null)
            {
                MenuInvoices.Header =
                    L("Invoices", "Invoices");
            }

            if (MenuPOS != null)
            {
                MenuPOS.Header =
                    L("POS", "POS");
            }

            if (MenuOrders != null)
            {
                MenuOrders.Header =
                    L("Orders", "Orders");
            }

            if (MenuCustomers != null)
            {
                MenuCustomers.Header =
                    L("Customers", "Customers");
            }

            if (MenuSalesRep != null)
            {
                MenuSalesRep.Header =
                    L("SalesRep", "Sales Representatives");
            }

            if (MenuManufacturing != null)
            {
                MenuManufacturing.Header =
                    L("Manufacturing", "Manufacturing");
            }

            if (MenuBanks != null)
            {
                MenuBanks.Header =
                    L("Banks", "Banks");
            }

            if (MenuReports != null)
            {
                MenuReports.Header =
                    L("Reports", "Reports");
            }

            if (MenuTools != null)
            {
                MenuTools.Header =
                    L("Tools", "Tools");
            }

            if (MenuView != null)
            {
                MenuView.Header =
                    L("View", "View");
            }

            if (MenuExit != null)
            {
                MenuExit.Header =
                    L("Exit", "Exit");
            }

            if (MenuHelp != null)
            {
                MenuHelp.Header =
                    L("Help", "Help");
            }

            if (RibbonImportDataButton != null)
            {
                RibbonImportDataButton.Label =
                    AppServiceHost.Localization.GetString(
                        "Menu.Import",
                        "Import Data");
            }

            if (RibbonExportDataButton != null)
            {
                RibbonExportDataButton.Label =
                    AppServiceHost.Localization.GetString(
                        "Menu.Export",
                        "Export Data");
            }
        }

        private void OnRibbonButtonClick(
            object sender,
            RoutedEventArgs e)
        {
            CollapseRibbon();

            if (sender is not RibbonButton button)
                return;

            // View > Information > Info window
            // opens the shared information window from the main menu surface.
            if (string.Equals(
                    LocProps.GetKey(button),
                    "View.InfoWindow",
                    StringComparison.Ordinal))
            {
                try
                {
                    InfoWin.ShowLast(this);
                }
                catch (Exception ex)
                {
                    AppHost.Errors.Report(
                        ErrorCodes.ShellOperationFailed,
                        ex,
                        "MWin.xaml.View.InfoWindow");
                }

                return;
            }

            // Tools > Options opens the general Options window
            // (services phase B1); the POS device settings keep their
            // own window, reachable from OptWin's POS section.
            if (string.Equals(LocProps.GetKey(button), "Tools.Options", StringComparison.Ordinal))
            {
                var options = new OptWin { Owner = this };
                options.ShowDialog();
                return;
            }

            // =====================================================
            // Backup
            // =====================================================

            if (ReferenceEquals(
                    button,
                    RibbonBackupButton))
            {
                OpenBackupWindow();
                return;
            }

            // =====================================================
            // Restore Backup
            // =====================================================

            if (ReferenceEquals(
                    button,
                    RibbonRestoreBackupButton))
            {
                OpenBackupWindow();
                return;
            }

            // =====================================================
            // Category Card (Inventory)
            // =====================================================

            if (ReferenceEquals(
                    button,
                    RibbonCategoryCardButton))
            {
                OpenCategoryCard();
                return;
            }

			// =====================================================
            // Warehouse card(Inventory)
            // =====================================================
			
			if (ReferenceEquals(
					button,
					RibbonWarehouseCardButton))
			{
				OpenWarehouseCard();
				return;
			}
			

            // =====================================================
            // Chart of Accounts
            // =====================================================

            if (ReferenceEquals(
                    button,
                    RibbonChartOfAccountsButton))
            {
                OpenChartOfAccounts();
                return;
            }

            // =====================================================
            // Account Card
            // =====================================================

            if (ReferenceEquals(
                    button,
                    RibbonAccountCardButton))
            {
                OpenAccountCard();
                return;
            }

            // =====================================================
            // Material Card (Inventory)
            // =====================================================

            if (ReferenceEquals(
                    button,
                    RibbonMaterialCardButton))
            {
                OpenMaterialCard();
                return;
            }

            // =====================================================
            // Material Groups (Inventory)
            // =====================================================

            if (ReferenceEquals(
                    button,
                    RibbonMaterialGroupsButton))
            {
                OpenMaterialGroups();
                return;
            }

            if (ReferenceEquals(
                    button,
                    RibbonMaterialTreeButton))
            {
                OpenMaterialDirectory();
                return;
            }

            if (string.Equals(
                    LocProps.GetKey(button),
                    "Inventory.InventoryAdjustment",
                    StringComparison.Ordinal))
            {
                WhsWin.OpenStockCount(this, isAdjustment: true);
                return;
            }

            if (string.Equals(
                    LocProps.GetKey(button),
                    "Inventory.TransferMaterial",
                    StringComparison.Ordinal))
            {
                MatMergeWin.Open(this);
                return;
            }

            // =====================================================
            // Import
            // =====================================================

            if (ReferenceEquals(
                    button,
                    RibbonImportDataButton))
            {
                OpenDataWindow(
                    exportMode: false);

                return;
            }

            // =====================================================
            // Export
            // =====================================================

            if (ReferenceEquals(
                    button,
                    RibbonExportDataButton))
            {
                OpenDataWindow(
                    exportMode: true);

                return;
            }

            // =====================================================
            // Reports / Invoices / commercial documents (Batch 2+)
            // =====================================================

            {
                var reportKey = LocProps.GetKey(button);
                if (RibbonReportRouter.TryHandle(reportKey, this))
                    return;
                if (RibbonNav.TryHandle(reportKey, this))
                    return;
                if (RptNav.TryHandle(reportKey, this))
                    return;
            }

            // =====================================================
            // Generic Ribbon action
            // =====================================================

            var tab =
                FindParentRibbonTab(button);

            if (DataContext is MainShVM viewModel)
            {
                // Always dispatch Ribbon actions by their stable
                // localization key. The visible label changes
                // with the selected language.
                var actionKey =
                    LocProps.GetKey(button);

                viewModel.HandleRibbonAction(
                    button.Label,
                    actionKey,
                    tab?.Name,
                    tab?.Header?.ToString());
            }
        }

        private void OpenBackupWindow()
        {
            if (_bkpWindow is not null)
            {
                if (_bkpWindow.WindowState ==
                    WindowState.Minimized)
                {
                    _bkpWindow.WindowState =
                        WindowState.Normal;
                }

                _bkpWindow.Activate();
                _bkpWindow.Focus();

                return;
            }

            _bkpWindow = new BkpWin
            {
                Owner = this,
                WindowStartupLocation =
                    WindowStartupLocation.CenterOwner
            };

            _bkpWindow.Closed += (_, _) =>
            {
                _bkpWindow = null;
                ActivateMainWindow();
            };

            _bkpWindow.Show();
            _bkpWindow.Activate();
        }

        private void ApplyWindowPreferences()
        {
            try
            {
                var pref = UsrPref.Load();

                pref.ApplyToWindow(this);
            }
            catch (Exception exL914)
            {
                AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, exL914, "MWin.xaml.ApplyWindowPreferences");
                // Non-fatal.
            }
        }

        protected override void OnClosed(
            EventArgs e)
        {
            _isClosing = true;

            try
            {
                var pref = UsrPref.Load();

                pref.SaveFromWindow(this);
                pref.Save();
            }
            catch (Exception exL932)
            {
                AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, exL932, "MWin.xaml.OnClosed");
                // Non-fatal.
            }

            base.OnClosed(e);
        }

        private void ActivateMainWindow()
        {
            if (_isClosing || !IsVisible)
                return;

            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    if (_isClosing || !IsVisible)
                        return;

                    if (WindowState ==
                        WindowState.Minimized)
                    {
                        WindowState =
                            WindowState.Normal;
                    }

                    Activate();
                    Focus();

                    try
                    {
                        var handle =
                            new WindowInteropHelper(this).Handle;

                        if (handle != IntPtr.Zero)
                        {
                            SetForegroundWindow(handle);
                        }
                    }
                    catch (Exception exL972)
                    {
                        AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, exL972, "MWin.xaml.ActivateMainWindow");
                        // Foreground activation is best-effort.
                    }
                }));
        }

        private void OnSourceInitialized(
            object? sender,
            EventArgs e)
        {
            var source =
                PresentationSource.FromVisual(this)
                as HwndSource;

            source?.AddHook(WindowProc);
        }

        private IntPtr WindowProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            const int MONITOR_DEFAULTTONEAREST = 0x00000002;

            if (msg != WM_GETMINMAXINFO)
                return IntPtr.Zero;

            var monitor =
                MonitorFromWindow(
                    hwnd,
                    MONITOR_DEFAULTTONEAREST);

            if (monitor == IntPtr.Zero)
                return IntPtr.Zero;

            var info =
                new MONITORINFO
                {
                    cbSize =
                        Marshal.SizeOf<MONITORINFO>()
                };

            if (!GetMonitorInfo(
                    monitor,
                    ref info))
            {
                return IntPtr.Zero;
            }

            var mmi =
                Marshal.PtrToStructure<MINMAXINFO>(
                    lParam);

            mmi.ptMaxPosition.X =
                info.rcWork.Left -
                info.rcMonitor.Left;

            mmi.ptMaxPosition.Y =
                info.rcWork.Top -
                info.rcMonitor.Top;

            mmi.ptMaxSize.X =
                info.rcWork.Right -
                info.rcWork.Left;

            mmi.ptMaxSize.Y =
                info.rcWork.Bottom -
                info.rcWork.Top;

            mmi.ptMaxTrackSize.X =
                mmi.ptMaxSize.X;

            mmi.ptMaxTrackSize.Y =
                mmi.ptMaxSize.Y;

            // This hook fully replaces WPF's own WM_GETMINMAXINFO handling (handled = true
            // below), which means WPF never gets a chance to translate MinWidth/MinHeight
            // into ptMinTrackSize itself. Without this, the window could be dragged smaller
            // than MinWidth/MinHeight — do that conversion here instead, in physical pixels.
            var dpi = VisualTreeHelper.GetDpi(this);

            if (MinWidth > 0)
            {
                mmi.ptMinTrackSize.X =
                    (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
            }

            if (MinHeight > 0)
            {
                mmi.ptMinTrackSize.Y =
                    (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
            }

            Marshal.StructureToPtr(
                mmi,
                lParam,
                false);

            handled = true;

            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(
            LayoutKind.Sequential,
            CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(
            IntPtr hwnd,
            uint dwFlags);

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(
            IntPtr hMonitor,
            ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(
            IntPtr hWnd);

        // -------------------------------------------------------------
        // Explorer-style shell (visual phase V3)
        //
        // The navigation pane + command bar are a second presentation
        // of the SAME ribbon map: both are generated from the ribbon
        // tabs/groups/buttons declared in MWin.xaml, and every item
        // routes through OnRibbonButtonClick with the ORIGINAL
        // RibbonButton as sender, so routing, localization keys and
        // behavior are identical to the retired ribbon strip. The
        // ribbon control stays in the tree (permanently collapsed):
        // shell state code (freeze/unfreeze, GetVisualDescendants,
        // FindName) keeps operating on the same objects, and the
        // generated items mirror IsEnabled/Visibility by binding.
        // -------------------------------------------------------------

        // Tab name -> menu localization key (mirrors UiLoczr's
        // RibbonTabKeys so pane headers localize identically).
        private static readonly Dictionary<string, string> NavTabKeys =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["TabFile"] = "Menu.File",
                ["TabInventory"] = "Menu.Inventory",
                ["TabAccounts"] = "Menu.Accounts",
                ["TabBonds"] = "Menu.Bonds",
                ["TabInvoices"] = "Menu.Invoices",
                ["TabPOS"] = "Menu.POS",
                ["TabOrders"] = "Menu.Orders",
                ["TabCustomers"] = "Menu.Customers",
                ["TabSalesRep"] = "Menu.SalesRep",
                ["TabManufacturing"] = "Menu.Manufacturing",
                ["TabHR"] = "Menu.HR",
                ["TabBanks"] = "Menu.Banks",
                ["TabReports"] = "Menu.Reports",
                ["TabTools"] = "Menu.Tools",
                ["TabView"] = "Menu.View",
                ["TabExit"] = "Menu.Exit",
                ["TabHelp"] = "Menu.Help"
            };

        private readonly Dictionary<string, (Button Header, StackPanel Body)> _navSections =
            new(StringComparer.OrdinalIgnoreCase);

        // Deferred nav bodies (docs/STARTUP_PERF_STUDY.md L4):
        // BuildNavShell creates every section header plus the first
        // section's body; each remaining body is populated on first
        // demand (header click / ExpandNavSection) or by a
        // Loaded-priority pass right after first render, whichever
        // comes first. The controls, bindings and behavior are
        // identical — only their construction time moves out of the
        // constructor and off the show path.
        private readonly Dictionary<RibbonTab, StackPanel> _navBodyPanels = new();
        private readonly HashSet<RibbonTab> _navBodiesBuilt = new();

        private void BuildNavShell()
        {
            NavPaneItems.Children.Clear();
            _navSections.Clear();
            _navBodyPanels.Clear();
            _navBodiesBuilt.Clear();

            var firstSection = true;
            foreach (var tab in RibbonControl.Items.OfType<RibbonTab>())
            {
                var body = new StackPanel();
                _navBodyPanels[tab] = body;

                var headerText = tab.Header?.ToString() ?? tab.Name;
                string? headerKey = null;
                if (!string.IsNullOrEmpty(tab.Name) &&
                    NavTabKeys.TryGetValue(tab.Name, out var menuKey))
                {
                    headerKey = menuKey;
                    headerText = Loc.Get(menuKey, headerText);
                }

                var header = new Button { Content = headerText };
                header.SetResourceReference(
                    StyleProperty, "NavSectionHeaderStyle");
                if (headerKey != null)
                    LocProps.SetKey(header, headerKey);
                BindingOperations.SetBinding(
                    header, IsEnabledProperty,
                    new Binding("IsEnabled") { Source = tab });

                var capturedTab = tab;
                var capturedBody = body;
                header.Click += (_, _) =>
                {
                    EnsureNavBody(capturedTab);
                    capturedBody.Visibility =
                        capturedBody.Visibility == Visibility.Visible
                            ? Visibility.Collapsed
                            : Visibility.Visible;
                };

                var section = new StackPanel();
                section.Children.Add(header);
                section.Children.Add(body);
                NavPaneItems.Children.Add(section);

                if (!string.IsNullOrEmpty(tab.Name))
                    _navSections[tab.Name] = (header, body);

                if (firstSection)
                {
                    firstSection = false;
                    EnsureNavBody(tab);
                }
            }

            if (_navBodiesBuilt.Count < _navBodyPanels.Count)
            {
                Dispatcher.BeginInvoke(
                    new Action(BuildRemainingNavBodies),
                    DispatcherPriority.Loaded);
            }
        }

        private void EnsureNavBody(RibbonTab tab)
        {
            if (!_navBodyPanels.TryGetValue(tab, out var body))
                return;
            if (!_navBodiesBuilt.Add(tab))
                return;
            PopulateNavBody(tab, body);
        }

        private void EnsureNavBodyByName(string tabName)
        {
            foreach (var tab in _navBodyPanels.Keys)
            {
                if (string.Equals(
                        tab.Name, tabName, StringComparison.OrdinalIgnoreCase))
                {
                    EnsureNavBody(tab);
                    return;
                }
            }
        }

        private void BuildRemainingNavBodies()
        {
            if (_isClosing)
                return;

            try
            {
                foreach (var tab in _navBodyPanels.Keys.ToList())
                    EnsureNavBody(tab);
            }
            catch (Exception exL1304)
            {
                AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, exL1304, "MWin.xaml.BuildNavShell");
                // Non-fatal: any body not built here is still built
                // on first demand (header click / ExpandNavSection).
            }
        }

        private void PopulateNavBody(RibbonTab tab, StackPanel body)
        {
            foreach (var group in tab.Items.OfType<RibbonGroup>())
            {
                var groupLabel = new TextBlock
                {
                    Text = group.Header?.ToString() ?? string.Empty,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Opacity = 0.65,
                    Margin = new Thickness(10, 8, 8, 2)
                };
                var groupKey = LocProps.GetKey(group);
                if (!string.IsNullOrWhiteSpace(groupKey))
                    LocProps.SetKey(groupLabel, groupKey);
                BindingOperations.SetBinding(
                    groupLabel, VisibilityProperty,
                    new Binding("Visibility") { Source = group });
                body.Children.Add(groupLabel);

                foreach (var source in FindRibbonButtons(group))
                    body.Children.Add(CreateShellItem(
                        source, "NavPaneItemStyle"));
            }
        }

        private Button CreateShellItem(RibbonButton source, string styleKey)
        {
            var item = new Button
            {
                Content = source.Label ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            item.SetResourceReference(StyleProperty, styleKey);

            var key = LocProps.GetKey(source);
            if (!string.IsNullOrWhiteSpace(key))
                LocProps.SetKey(item, key);
            var tipKey = LocProps.GetToolTipKey(source);
            if (!string.IsNullOrWhiteSpace(tipKey))
                LocProps.SetToolTipKey(item, tipKey);

            BindingOperations.SetBinding(
                item, IsEnabledProperty,
                new Binding("IsEnabled") { Source = source });
            BindingOperations.SetBinding(
                item, VisibilityProperty,
                new Binding("Visibility") { Source = source });

            item.Click += (_, e) => OnRibbonButtonClick(source, e);
            return item;
        }

        private static IEnumerable<RibbonButton> FindRibbonButtons(
            DependencyObject root)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is RibbonButton button)
                    yield return button;
                else if (child is DependencyObject dep)
                    foreach (var nested in FindRibbonButtons(dep))
                        yield return nested;
            }
        }

        private void BuildCommandBar(RibbonTab tab)
        {
            CommandBarItems.Children.Clear();

            var firstGroup = true;
            foreach (var group in tab.Items.OfType<RibbonGroup>())
            {
                if (!firstGroup)
                {
                    var separator = new Border
                    {
                        Width = 1,
                        Margin = new Thickness(6)
                    };
                    separator.SetResourceReference(
                        Border.BackgroundProperty,
                        "ShellContentBorderBrush");
                    CommandBarItems.Children.Add(separator);
                }
                firstGroup = false;

                foreach (var source in FindRibbonButtons(group))
                    CommandBarItems.Children.Add(CreateShellItem(
                        source, "NavCommandButtonStyle"));
            }

            var title = tab.Header?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(tab.Name) &&
                NavTabKeys.TryGetValue(tab.Name, out var titleKey))
            {
                CommandBarTitle.Text = Loc.Get(titleKey, title);
                LocProps.SetKey(CommandBarTitle, titleKey);
            }
            else
            {
                CommandBarTitle.Text = title;
            }
        }

        private void ExpandNavSection(string tabName)
        {
            if (NavPane.Visibility != Visibility.Visible)
                NavPane.Visibility = Visibility.Visible;

            EnsureNavBodyByName(tabName);

            if (_navSections.TryGetValue(tabName, out var section))
            {
                section.Body.Visibility = Visibility.Visible;
                section.Header.BringIntoView();
            }
        }

        private void OnNavToggleClick(object sender, RoutedEventArgs e)
        {
            NavPane.Visibility = NavPane.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ShowRibbonTab(
            string tabName)
        {
            if (RibbonControl == null)
                return;

            foreach (RibbonTab tab in RibbonControl.Items)
                tab.Visibility = Visibility.Collapsed;

            if (_currentTab == tabName &&
                RibbonControl.Visibility == Visibility.Visible)
            {
                RibbonControl.Visibility = Visibility.Collapsed;
                _currentTab = string.Empty;
                return;
            }

            if (FindName(tabName) is RibbonTab selectedTab)
            {
                selectedTab.Visibility = Visibility.Visible;
                selectedTab.IsSelected = true;
                RibbonControl.Visibility = Visibility.Visible;
                _currentTab = tabName;
            }
        }

        private void OnMenuFileClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabFile");

        private void OnMenuInventoryClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabInventory");

        private void OnMenuAccountsClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabAccounts");

        private void OnMenuBondsClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabBonds");

        private void OnMenuInvoicesClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabInvoices");

        private void OnMenuPOSClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabPOS");

        private void OnMenuOrdersClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabOrders");

        private void OnMenuCustomersClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabCustomers");

        private void OnMenuSalesRepClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabSalesRep");

        private void OnMenuManufacturingClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabManufacturing");

        private void OnMenuHRClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabHR");

        private void OnMenuBanksClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabBanks");

        private void OnMenuReportsClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabReports");

        private void OnMenuToolsClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabTools");

        private void OnMenuViewClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabView");

        private void OnMenuExitClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabExit");

        private void OnMenuHelpClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabHelp");

        internal void FreezeForCompanySwitch()
        {
            // Recovery/company-switch mode:
            // keep the shell alive, but disable all
            // operational surfaces.
            // Only File -> Open and File -> Exit remain usable.

            MinimizeButton.IsEnabled = false;
            MaximizeButton.IsEnabled = false;
            CloseButton.IsEnabled = false;

            QuickAccessBar.IsEnabled = false;
            MainMenu.IsEnabled = false;

            foreach (var item in MainMenu.Items.OfType<MenuItem>())
                item.IsEnabled = false;

            RibbonControl.IsEnabled = true;
            // Explorer shell: the strip itself never returns; the
            // pre-login surface is the navigation pane, whose File
            // section mirrors the tab/button state set below.
            RibbonControl.Visibility = Visibility.Collapsed;

            foreach (var tab in
                     RibbonControl.Items.OfType<RibbonTab>())
            {
                tab.IsEnabled =
                    ReferenceEquals(tab, TabFile);
            }

            TabFile.Visibility = Visibility.Visible;
            TabFile.IsSelected = true;

            foreach (var button in
                     GetVisualDescendants<RibbonButton>(TabFile))
            {
                button.IsEnabled =
                    ReferenceEquals(
                        button,
                        RibbonOpenCompanyButton)
                    ||
                    ReferenceEquals(
                        button,
                        RibbonExitButton);
            }

            if (Content is Grid root)
            {
                foreach (UIElement child in root.Children)
                {
                    var row = Grid.GetRow(child);

                    if (row >= 4)
                        child.IsEnabled = false;
                }
            }

            // The row>=4 sweep above disabled the pane with the
            // content; the pane is the pre-login navigation surface
            // (its items mirror the frozen ribbon state), so it stays
            // enabled with the File section expanded.
            NavPane.IsEnabled = true;
            ExpandNavSection("TabFile");

            MainStatusBar.IsEnabled = false;
            InternalWindowDock.IsEnabled = false;
        }

        internal void EnterRestoreRecoveryMode() =>
            FreezeForCompanySwitch();

        internal void UnfreezeAfterCompanyLogin()
        {
            MinimizeButton.IsEnabled = true;
            MaximizeButton.IsEnabled = true;
            CloseButton.IsEnabled = true;

            MainMenu.IsEnabled = true;
            QuickAccessBar.IsEnabled = true;
            RibbonControl.IsEnabled = true;
            MainStatusBar.IsEnabled = true;
            InternalWindowDock.IsEnabled = true;

            foreach (var item in
                     MainMenu.Items.OfType<MenuItem>())
            {
                item.IsEnabled = true;
            }

            foreach (var tab in
                     RibbonControl.Items.OfType<RibbonTab>())
            {
                tab.IsEnabled = true;
            }

            foreach (var button in
                     GetVisualDescendants<RibbonButton>(
                         RibbonControl))
            {
                button.IsEnabled = true;
            }

            RibbonControl.Visibility =
                Visibility.Collapsed;

            _currentTab = string.Empty;

            if (Content is Grid root)
            {
                foreach (UIElement child in root.Children)
                {
                    var row =
                        Grid.GetRow(child);

                    if (row >= 4)
                        child.IsEnabled = true;
                }
            }
        }

        private static IEnumerable<T> GetVisualDescendants<T>(
            DependencyObject root)
            where T : DependencyObject
        {
            if (root == null)
                yield break;

            for (var i = 0;
                 i < VisualTreeHelper.GetChildrenCount(root);
                 i++)
            {
                var child =
                    VisualTreeHelper.GetChild(
                        root,
                        i);

                if (child is T match)
                    yield return match;

                foreach (var descendant in
                         GetVisualDescendants<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        private void OpenAccountCard()
        {
            var window =
                new AccCard
                {
                    Owner = this,
                    WindowStartupLocation =
                        WindowStartupLocation.CenterOwner
                };

            // AccCard is modal.
            // When it closes, restore MWin as the active shell.
            window.ShowDialog();

            ActivateMainWindow();
        }

        private void OnRequestOpenMaterialCard(
            object? sender,
            EventArgs e) =>
            OpenMaterialCard();

        private void OpenMaterialCard()
        {
            try
            {
                foreach (Window open in WpfApp.Current.Windows)
                {
                    if (open is MatCard existing &&
                        existing.IsVisible)
                    {
                        if (existing.WindowState ==
                            WindowState.Minimized)
                        {
                            existing.WindowState =
                                WindowState.Normal;
                        }

                        existing.Activate();
                        existing.Focus();

                        return;
                    }
                }

                var window = new MatCard
                {
                    Owner = this,
                    WindowStartupLocation =
                        WindowStartupLocation.CenterOwner
                };

                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, ex, "MWin.xaml.OpenMaterialCard");
                MessageBox.Show(
                    this,
                    "Couldn't Open Materials card:\n" +
                    ex.Message,
                    "ShouTech ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void OpenCategoryCard()
        {
            try
            {
                foreach (Window w in WpfApp.Current.Windows)
                {
                    if (w is CatWin existing &&
                        existing.IsVisible)
                    {
                        if (existing.WindowState ==
                            WindowState.Minimized)
                        {
                            existing.WindowState =
                                WindowState.Normal;
                        }

                        existing.Activate();
                        existing.Focus();

                        return;
                    }
                }

                var win = new CatWin
                {
                    Owner = this,
                    WindowStartupLocation =
                        WindowStartupLocation.CenterOwner
                };

                win.Show();
                win.Activate();
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, ex, "MWin.xaml.OpenCategoryCard");
                MessageBox.Show(
                    this,
                    ex.Message,
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

		private void OpenWarehouseCard()
		{
			foreach (Window w in WpfApp.Current.Windows)
			{
				if (w is WhsWin existing && existing.IsVisible)
				{
					if (existing.WindowState == WindowState.Minimized)
						existing.WindowState = WindowState.Normal;
					existing.Activate();
					existing.Focus();
					return;
				}
			}

			var win = new WhsWin
			{
				Owner = this,
				WindowStartupLocation = WindowStartupLocation.CenterOwner
			};
			win.Show();
			win.Activate();
		}

        private void OnRequestOpenMaterialDirectory(
            object? sender,
            EventArgs e) =>
            OpenMaterialDirectory();

        private void OpenMaterialDirectory()
        {
            foreach (Window w in
                     System.Windows.Application.Current.Windows)
            {
                if (w is MatDirWin existing &&
                    existing.IsVisible)
                {
                    existing.Activate();

                    if (existing.WindowState ==
                        WindowState.Minimized)
                    {
                        existing.WindowState =
                            WindowState.Normal;
                    }

                    return;
                }
            }

            var window = new MatDirWin
            {
                Owner = this,
                WindowStartupLocation =
                    WindowStartupLocation.CenterOwner
            };

            window.Show();
        }

        private void OnRequestOpenMaterialGroups(
            object? sender,
            EventArgs e) =>
            OpenMaterialGroups();

        private void OpenMaterialGroups()
        {
            var existing = WpfApp.Current.Windows
                .OfType<MatGroupWin>()
                .FirstOrDefault(window => window.IsVisible);

            if (existing is not null)
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;

                existing.Activate();
                existing.Focus();
                return;
            }

            var window = new MatGroupWin
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            window.Show();
            window.Activate();
        }

        private void OpenDataWindow(
            bool exportMode)
        {
            // Step 11 (sweep S-3): the cached window is reactivated ONLY
            // when it is in the REQUESTED mode — DatWin's ModeKey tracks
            // its current mode, including live switches through its
            // in-window mode cards. A visible DatWin in a DIFFERENT mode
            // no longer swallows the request (File → Export used to land
            // on an Import window); a second instance opens in the
            // requested mode, per the central WindowActivator policy.
            var requestedMode = exportMode ? "StructuredExport" : "Import";
            if (_datWindow != null)
            {
                if (_datWindow.IsVisible)
                {
                    if (string.Equals(
                            ((IWindowMode)_datWindow).ModeKey,
                            requestedMode,
                            StringComparison.Ordinal))
                    {
                        if (_datWindow.WindowState ==
                            WindowState.Minimized)
                        {
                            _datWindow.WindowState =
                                WindowState.Normal;
                        }

                        _datWindow.Activate();
                        _datWindow.Focus();

                        return;
                    }

                    // A same-mode instance may already be open beside
                    // the cached different-mode one.
                    if (WindowActivator.TryActivateExisting<DatWin>(requestedMode))
                        return;
                }
                else
                {
                    _datWindow = null;
                }
            }
            else if (WindowActivator.TryActivateExisting<DatWin>(requestedMode))
            {
                return;
            }

            var window =
                new DatWin(exportMode)
                {
                    Owner = this,
                    WindowStartupLocation =
                        WindowStartupLocation.CenterOwner
                };

            _datWindow = window;

            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(
                        _datWindow,
                        window))
                {
                    _datWindow = null;
                }

                ActivateMainWindow();
            };

            window.Show();
            window.Activate();
        }

        private void OpenChartOfAccounts()
        {
            if (_coaWindow != null)
            {
                if (_coaWindow.IsVisible)
                {
                    if (_coaWindow.WindowState ==
                        WindowState.Minimized)
                    {
                        _coaWindow.WindowState =
                            WindowState.Normal;
                    }

                    _coaWindow.Activate();
                    _coaWindow.Focus();

                    return;
                }

                _coaWindow = null;
            }

            var window =
                new CoaWin
                {
                    Owner = this,
                    WindowStartupLocation =
                        WindowStartupLocation.CenterOwner
                };

            _coaWindow = window;

            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(
                        _coaWindow,
                        window))
                {
                    _coaWindow = null;
                }

                ActivateMainWindow();
            };

            window.Show();
            window.Activate();
        }

        private void CollapseRibbon()
        {
            if (RibbonControl == null)
                return;

            RibbonControl.Visibility = Visibility.Collapsed;
            _currentTab = string.Empty;
        }

        private static RibbonTab? FindParentRibbonTab(
            DependencyObject? child)
        {
            while (child != null)
            {
                if (child is RibbonTab tab)
                    return tab;

                child =
                    VisualTreeHelper.GetParent(child);
            }

            return null;
        }

        private void SwitchLanguage(
            string languageCode)
        {
            try
            {
                var code =
                    ShouTech.App.Services
                        .LanguageDirectionService
                        .NormalizeLanguageCode(
                            languageCode);

                if (string.IsNullOrWhiteSpace(code))
                    return;

                // Apply language safely
                // (culture + resources + deferred UI).
                App.SetApplicationLanguage(code);

                try
                {
                    var pref = UsrPref.Load();

                    pref.Lang = code;
                    pref.LangSet = true;

                    pref.Save();
                }
                catch (Exception exL1743)
                {
                    AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, exL1743, "MWin.xaml.SwitchLanguage");
                    // Preference saving is non-critical.
                }

                // Refresh menu headers on this window
                // after deferred culture switch.
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(
                        RefreshLocalizedText));
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, ex, "MWin.xaml.SwitchLanguage");
                MessageBox.Show(
                    $"Could not switch language: {ex.Message}",
                    "ShouTech ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void OnLanguageArabicClick(
            object sender,
            RoutedEventArgs e) =>
            SwitchLanguage("ar");

        private void OnLanguageEnglishClick(
            object sender,
            RoutedEventArgs e) =>
            SwitchLanguage("en");

        private void OnLanguageFrenchClick(
            object sender,
            RoutedEventArgs e) =>
            SwitchLanguage("fr");

        private void OnLanguageTurkishClick(
            object sender,
            RoutedEventArgs e) =>
            SwitchLanguage("tr");
    }
}