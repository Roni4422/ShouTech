using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Ribbon;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ShouTech.App.Configuration;
using ShouTech.App.Localization;
using ShouTech.App.Services;
using ShouTech.App.ViewModels;
using WpfApp = System.Windows.Application;

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
                VisuId.ApplyWindow(this);
                ApplyUiDirection();
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

                // Apply language direction safely.
                try
                {
                    LangDirSv.ApplyToWindow(this);
                }
                catch
                {
                    // Non-fatal.
                }

                // Apply localization keys to ribbon/menu.
                try
                {
                    UiLoczr.ApplyToWindow(this);
                }
                catch
                {
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
                MessageBox.Show(
                    $"خطأ: {ex.Message}",
                    "ShouTech ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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
            catch
            {
                // Non-fatal.
            }

            ApplyUiDirection();
            RefreshLocalizedText();

            try
            {
                UiLoczr.ApplyToWindow(this);
            }
            catch
            {
                // Non-fatal.
            }
        }

        private void ApplyUiDirection()
        {
            var language = AppHost.Localization.GetCurrentLanguage();
            var direction =
                LanguageDirectionService.GetFlowDirection(language);
            var isArabic = IsArabicUi();

            FlowDirection = direction;

            if (MainMenu != null)
                MainMenu.FlowDirection = direction;

            if (MainToolBar != null)
                MainToolBar.FlowDirection = direction;

            if (RibbonControl != null)
                RibbonControl.FlowDirection = direction;

            if (MainStatusBar != null)
                MainStatusBar.FlowDirection = direction;

            // Bin corner: Arabic → left, other languages → right (explicit product rule).
            ApplyRecycleBinCorner(isArabic);
        }

        private void ApplyRecycleBinCorner(bool isArabic)
        {
            PositionRecycleBinCorner(isArabic);
        }

        private void RecycleBinLayer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            PositionRecycleBinCorner(IsArabicUi());
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

        /// <summary>
        /// Places the archive-bin icon on a physical screen side.
        /// Arabic → left; other languages → right.
        /// Uses an LTR Canvas so RTL window FlowDirection cannot mirror Left/Right.
        /// </summary>
        private void PositionRecycleBinCorner(bool isArabic)
        {
            if (RecycleBinCornerButton is null || RecycleBinLayer is null)
                return;

            const double margin = 24;
            const double iconWidth = 120;

            RecycleBinCornerButton.FlowDirection = isArabic
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
            Canvas.SetBottom(RecycleBinCornerButton, margin);

            var layerWidth = RecycleBinLayer.ActualWidth;
            if (layerWidth <= 0)
                layerWidth = ActualWidth;

            if (isArabic)
            {
                Canvas.SetLeft(RecycleBinCornerButton, margin);
            }
            else
            {
                var left = Math.Max(margin, layerWidth - iconWidth - margin);
                Canvas.SetLeft(RecycleBinCornerButton, left);
            }

            if (BinStatusItem != null)
            {
                // Status bar item: still subject to bar FlowDirection; keep explicit.
                BinStatusItem.HorizontalAlignment = isArabic
                    ? HorizontalAlignment.Left
                    : HorizontalAlignment.Right;
                BinStatusItem.HorizontalContentAlignment = BinStatusItem.HorizontalAlignment;
            }
        }

        private bool _recycleBinOpen;

        private async void OnRecycleBinClick(object sender, RoutedEventArgs e)
        {
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

                if (RecycleBinButton != null)
                    RecycleBinButton.ToolTip = tooltip;

                if (RecycleBinCornerButton != null)
                    RecycleBinCornerButton.ToolTip = tooltip;

            }
            catch
            {
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
            catch (InvalidOperationException)
            {
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
                    ? "استعادة"
                    : "تكبير";
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
                    L("File", "ملف");
            }

            if (MenuInventory != null)
            {
                MenuInventory.Header =
                    L("Inventory", "مستودعات");
            }

            if (MenuAccounts != null)
            {
                MenuAccounts.Header =
                    L("Accounts", "حسابات");
            }

            if (MenuBonds != null)
            {
                MenuBonds.Header =
                    L("Bonds", "سندات");
            }

            if (MenuInvoices != null)
            {
                MenuInvoices.Header =
                    L("Invoices", "فواتير");
            }

            if (MenuPOS != null)
            {
                MenuPOS.Header =
                    L("POS", "نقاط بيع");
            }

            if (MenuOrders != null)
            {
                MenuOrders.Header =
                    L("Orders", "طلبيات");
            }

            if (MenuCustomers != null)
            {
                MenuCustomers.Header =
                    L("Customers", "عملاء");
            }

            if (MenuSalesRep != null)
            {
                MenuSalesRep.Header =
                    L("SalesRep", "مندوب");
            }

            if (MenuManufacturing != null)
            {
                MenuManufacturing.Header =
                    L("Manufacturing", "تصنيع");
            }

            if (MenuBanks != null)
            {
                MenuBanks.Header =
                    L("Banks", "بنوك");
            }

            if (MenuReports != null)
            {
                MenuReports.Header =
                    L("Reports", "تقارير");
            }

            if (MenuTools != null)
            {
                MenuTools.Header =
                    L("Tools", "أدوات");
            }

            if (MenuOtherTools != null)
            {
                MenuOtherTools.Header =
                    L("OtherTools", "أدوات أخرى");
            }

            if (MenuView != null)
            {
                MenuView.Header =
                    L("View", "عرض");
            }

            if (MenuExit != null)
            {
                MenuExit.Header =
                    L("Exit", "خروج");
            }

            if (MenuHelp != null)
            {
                MenuHelp.Header =
                    L("Help", "مساعدة");
            }

            if (RibbonImportDataButton != null)
            {
                RibbonImportDataButton.Label =
                    AppServiceHost.Localization.GetString(
                        "Menu.Import",
                        "استيراد بيانات");
            }

            if (RibbonExportDataButton != null)
            {
                RibbonExportDataButton.Label =
                    AppServiceHost.Localization.GetString(
                        "Menu.Export",
                        "تصدير بيانات");
            }
        }

        private void OnRibbonButtonClick(
            object sender,
            RoutedEventArgs e)
        {
            CollapseRibbon();

            if (sender is not RibbonButton button)
                return;

            // POS device settings live under Tools > Options.
            if (string.Equals(LocProps.GetKey(button), "Tools.Options", StringComparison.Ordinal))
            {
                var options = new CashDrawerOptionsWin { Owner = this };
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
            catch
            {
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
            catch
            {
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
                    catch
                    {
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

        private void ShowRibbonTab(
            string tabName)
        {
            if (RibbonControl == null)
                return;

            foreach (RibbonTab tab in RibbonControl.Items)
            {
                tab.Visibility =
                    Visibility.Collapsed;
            }

            if (_currentTab == tabName &&
                RibbonControl.Visibility ==
                    Visibility.Visible)
            {
                RibbonControl.Visibility =
                    Visibility.Collapsed;

                _currentTab = string.Empty;

                return;
            }

            if (FindName(tabName) is RibbonTab selectedTab)
            {
                selectedTab.Visibility =
                    Visibility.Visible;

                selectedTab.IsSelected = true;

                RibbonControl.Visibility =
                    Visibility.Visible;

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

        private void OnMenuOtherToolsClick(
            object sender,
            RoutedEventArgs e) =>
            ShowRibbonTab("TabOtherTools");

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

            MainToolBar.IsEnabled = false;
            MainMenu.IsEnabled = false;

            foreach (var item in MainMenu.Items.OfType<MenuItem>())
                item.IsEnabled = false;

            RibbonControl.IsEnabled = true;
            RibbonControl.Visibility = Visibility.Visible;

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
            MainToolBar.IsEnabled = true;
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
            // Requires MatGroupWin + MatGroupRepo.
            // Copy from Optional_MaterialGroups if not installed.

            var existing =
                System.Windows.Application.Current.Windows
                    .OfType<Window>()
                    .FirstOrDefault(
                        w =>
                            w.GetType().Name ==
                            "MatGroupWin" &&
                            w.IsVisible);

            if (existing != null)
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

            try
            {
                var t =
                    Type.GetType(
                        "ShouTech.App.Views.MatGroupWin")
                    ??
                    AppDomain.CurrentDomain
                        .GetAssemblies()
                        .Select(
                            a =>
                                a.GetType(
                                    "ShouTech.App.Views.MatGroupWin"))
                        .FirstOrDefault(
                            x => x != null);

                if (t == null)
                {
                    MessageBox.Show(
                        this,
                        "نافذة مجموعات المواد غير مثبتة.\n" +
                        "انسخ MatGroupWin و MatGroupRepo من الحزمة.",
                        "ShouTech ERP",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    return;
                }

                var window =
                    (Window)Activator.CreateInstance(t)!;

                window.Owner = this;

                window.WindowStartupLocation =
                    WindowStartupLocation.CenterOwner;

                window.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "ShouTech ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void OpenDataWindow(
            bool exportMode)
        {
            if (_datWindow != null)
            {
                if (_datWindow.IsVisible)
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

                _datWindow = null;
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

            RibbonControl.Visibility =
                Visibility.Collapsed;

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
                catch
                {
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
                MessageBox.Show(
                    $"تعذر تبديل اللغة: {ex.Message}",
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