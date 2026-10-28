using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using Npgsql;
using ShouTech.App.Configuration;
using ShouTech.App.Common;
using ShouTech.App.Localization;
using ShouTech.Domain.Interfaces;
using ShouTech.App.Services;
using WpfApp = System.Windows.Application;

namespace ShouTech.App.Views
{
    public enum ComWinMode
    {
        Startup,
        Management
    }

    public partial class ComWin : Window, INotifyPropertyChanged
    {
        private readonly ComWinMode _mode;
        private ObservableCollection<CompanyItem> _companies = new();
        private CompanyItem? _selectedCompany;
        private StoredCoRec? _editingCompanyRecord;

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_SYSMENU = 0x00080000;

        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public ObservableCollection<CompanyItem> Companies
        {
            get => _companies;
            set { _companies = value; OnPropertyChanged(); }
        }

        public CompanyItem? SelectedCompany
        {
            get => _selectedCompany;
            set { _selectedCompany = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ComWin() : this(ComWinMode.Startup) { }

        public ComWin(ComWinMode mode)
        {
            _mode = mode;
            InitializeComponent();
            VisuId.ApplyWindow(this);
            ConfigureWindowChromeForMode();
            Resources["PreferCenterScreen"] = true;
            DataContext = this;
            ApplyWindowDirection();
            ApplyLayoutForMode();
            ApplyButtonOrder();
            ApplyLocalizedMgmtLabels();

            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += (_, _) =>
            {
                ApplyWindowDirection();
                ApplyLayoutForMode();
                ApplyButtonOrder();
                ApplyLocalizedMgmtLabels();
                UiLoczr.ApplyToWindow(this);
            };
            Loaded += async (_, _) => await LoadCompaniesAsync();
            AppServiceHost.Localization.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) => AppServiceHost.Localization.LanguageChanged -= OnLanguageChanged;
        }

        private void ConfigureWindowChromeForMode()
        {
            if (_mode == ComWinMode.Startup)
            {
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                AllowsTransparency = false;
                ShowInTaskbar = false;

                var chrome = WindowChrome.GetWindowChrome(this);
                if (chrome == null)
                {
                    chrome = new WindowChrome();
                    WindowChrome.SetWindowChrome(this, chrome);
                }
                chrome.CaptionHeight = 0;
                chrome.ResizeBorderThickness = new Thickness(0);
                chrome.GlassFrameThickness = new Thickness(0);
                chrome.UseAeroCaptionButtons = false;
                chrome.NonClientFrameEdges = NonClientFrameEdges.None;

                SourceInitialized += (_, _) => StripNativeCaption();

                if (TryFindResource("SurfaceBrush") is System.Windows.Media.Brush surface)
                {
                    Background = surface;
                    rootSurfaceGrid.Background = surface;
                }

                return;
            }

            Style = null;
            WindowChrome.SetWindowChrome(this, null);
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            AllowsTransparency = false;
            Width = 980;
            Height = 680;
           

            if (TryFindResource("SurfaceBrush") is System.Windows.Media.Brush surfaceMgmt)
            {
                Background = surfaceMgmt;
                rootSurfaceGrid.Background = surfaceMgmt;
            }
        }

        private void StripNativeCaption()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                long style;
                if (IntPtr.Size == 8)
                    style = GetWindowLongPtr64(hwnd, GWL_STYLE).ToInt64();
                else
                    style = GetWindowLong32(hwnd, GWL_STYLE);

                style &= ~WS_CAPTION;
                style &= ~WS_THICKFRAME;
                style &= ~WS_SYSMENU;

                bool applied;
				if (IntPtr.Size == 8)
				{
					var previous = SetWindowLongPtr64(hwnd, GWL_STYLE, new IntPtr(style));
					applied = previous != IntPtr.Zero || Marshal.GetLastWin32Error() == 0;
				}
				else
				{
					var previous = SetWindowLong32(hwnd, GWL_STYLE, (int)style);
					applied = previous != 0 || Marshal.GetLastWin32Error() == 0;
				}

				if (!applied)
				{
					System.Diagnostics.Debug.WriteLine(
						$"ComWin: SetWindowLong failed. Win32Error={Marshal.GetLastWin32Error()}");
					return;
				}

				SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
					SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            }
            catch { }
        }

        private void ApplyLayoutForMode()
        {
            var isMgmt = _mode == ComWinMode.Management;
            mgmtPanel.Visibility = isMgmt ? Visibility.Visible : Visibility.Collapsed;
            btnBar.Visibility = isMgmt ? Visibility.Collapsed : Visibility.Visible;

            if (!isMgmt)
            {
                mgmtPanel.Visibility = Visibility.Collapsed;
                editBtnBar.Visibility = editPanel.Visibility == Visibility.Visible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                return;
            }

            editBtnBar.Visibility = Visibility.Collapsed;
            rootDock.FlowDirection = FlowDirection.LeftToRight;
            contentGrid.FlowDirection = IsArabicUi() ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            if (IsArabicUi())
            {
                DockPanel.SetDock(mgmtPanel, Dock.Right);
                mgmtPanel.Margin = new Thickness(12, 0, 0, 0);
                mgmtPanel.FlowDirection = FlowDirection.RightToLeft;
            }
            else
            {
                DockPanel.SetDock(mgmtPanel, Dock.Left);
                mgmtPanel.Margin = new Thickness(0, 0, 12, 0);
                mgmtPanel.FlowDirection = FlowDirection.LeftToRight;
            }
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() =>
                {
                    ApplyWindowDirection();
                    ApplyLayoutForMode();
                    ApplyButtonOrder();
                    ApplyLocalizedMgmtLabels();
                    if (editPanel.Visibility == Visibility.Visible)
                        ApplyEditModeLabels();
                }));
        }

        private void ApplyLocalizedMgmtLabels()
        {
            Title = Loc.Get("CompanySelection.WindowTitle", "Company Selection");

            txtMgmtTitle.Text = Loc.Get("CompanyManagement.Title", "Company Management");
            btnMgmtOpen.Content = Loc.Get("Common.Open", "Open");
            btnMgmtEdit.Content = Loc.Get("Common.Edit", "Edit");
            if (txtDeleteLabel != null)
                txtDeleteLabel.Text = Loc.Get("Common.Delete", "Delete");
            btnMgmtDelete.ToolTip = Loc.Get("CompanyManagement.DeleteOptions", "Delete Options");
            if (miDeleteFromIndex != null)
                miDeleteFromIndex.Header = Loc.Get("CompanyManagement.DeleteFromWindow", "Remove from List");
            if (miDeleteFully != null)
                miDeleteFully.Header = Loc.Get("CompanyManagement.DeleteFully", "Delete Permanently");
            btnMgmtReindex.Content = Loc.Get("CompanyManagement.Reindex", "Reindex");
            btnMgmtClose.Content = Loc.Get("Common.Close", "Close");

            var isEditing = editPanel.Visibility == Visibility.Visible;
            txtTitle.Text = isEditing
                ? Loc.Get("CompanyEdit.Title", "Edit Company Details")
                : Loc.Get("CompanySelection.Title", "Company Selection");

            if (btnSetupWizard != null)
            {
                btnSetupWizard.Content = Loc.Get("CompanySelection.CreateCompany", "Create Company");
                btnSetupWizard.ToolTip = btnSetupWizard.Content;
            }

            btnOk.Content = Loc.Get("CompanySelection.Ok", "OK");
            btnCancel.Content = Loc.Get("Buttons.Cancel", "Cancel");

            txtHint.Text = isEditing
                ? Loc.Get("CompanyEdit.Hint", "Save changes or cancel to return to the list.")
                : _mode == ComWinMode.Management
                    ? Loc.Get("CompanySelection.MgmtHint", "Select a company, then use the side actions.")
                    : Loc.Get("CompanySelection.Hint", "Select a company to continue to sign in.");
        }

        private static bool IsArabicUi()
            => (ShouTech.App.App.CurrentLanguageCode ?? "en")
                .StartsWith("ar", StringComparison.OrdinalIgnoreCase);

        private void ApplyWindowDirection()
        {
            try { LangDirSv.ApplyToWindow(this, ShouTech.App.App.CurrentLanguageCode); } catch { }
        }

        private void ApplyButtonOrder()
        {
            try
            {
                Grid.SetColumn(btnSetupWizard, 0);
                Grid.SetColumn(btnOk, 2);
                Grid.SetColumn(btnCancel, 4);
            }
            catch { }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (_mode == ComWinMode.Management)
                    BtnMgmtOpen_Click(sender, e);
                else
                    _ = ContinueToLoginAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SafeCloseOrShutdown();
            }
        }

        private async Task LoadCompaniesAsync()
        {
            try
            {
                var cached = TryLoadCachedIndex();
                if (cached.Count > 0)
                {
                    PopulateCompanies(cached);
                }
                else
                {
                    var realCompanies = await new CoRegSvc().GetCompaniesAsync().ConfigureAwait(true);
                    var entries = (realCompanies ?? new List<CompanyInfo>())
                        .Where(c => c.Enabled)
                        .Select(c => new CompanyIndexEntry
                        {
                            Code = c.Code ?? string.Empty,
                            Name = c.Name ?? string.Empty,
                            NameEn = c.NameEn ?? string.Empty,
                            Enabled = c.Enabled
                        })
                        .ToList();
                    PopulateCompanies(entries);

                    _ = TryRefreshIndexInBackgroundAsync();
                }

                if (!Companies.Any())
                {
                    if (_mode == ComWinMode.Startup)
                    {
                        OpenSetupWizard();
                        return;
                    }

                    ShowNotice(Loc.Get("CompanySelection.NoCompanies",
                        "No companies are indexed yet. Use Reindex or create a company."));
                }

                var recentCompanies = await CoLocalStore.LoadAsync();
                ApplyRecentCompanyOrder(recentCompanies.Select(company => company.Code).ToList());
                SelectedCompany = Companies.FirstOrDefault();
                lstCompanies.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"{Loc.Get("CompanySelection.LoadErrorMessage", "Failed to load companies")}:\n{ex.Message}",
                    Loc.Get("Application.Error", "Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                if (_mode == ComWinMode.Startup)
                {
                    try
                    {
                        var pref = UsrPref.Load();
                        if (!pref.CompanySetupComplete)
                            OpenSetupWizard();
                    }
                    catch { OpenSetupWizard(); }
                }
            }
        }

        private static List<CompanyIndexEntry> TryLoadCachedIndex()
        {
            try
            {
                var path = AppPaths.CompaniesIndex;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return new List<CompanyIndexEntry>();

                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(nameof(Companies), out var companies) ||
                    companies.ValueKind != JsonValueKind.Array)
                    return new List<CompanyIndexEntry>();

                var result = new List<CompanyIndexEntry>();
                foreach (var c in companies.EnumerateArray())
                {
                    var code = c.TryGetProperty("Code", out var codeEl) ? codeEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(code)) continue;

                    var name = c.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    var nameEn = c.TryGetProperty("NameEn", out var nameEnEl) ? nameEnEl.GetString() ?? "" : "";
                    var enabled = !c.TryGetProperty("Enabled", out var enEl) || enEl.GetBoolean();

                    if (!enabled) continue;

                    result.Add(new CompanyIndexEntry
                    {
                        Code = code!,
                        Name = name,
                        NameEn = nameEn,
                        Enabled = enabled
                    });
                }
                return result;
            }
            catch
            {
                return new List<CompanyIndexEntry>();
            }
        }

        private static async Task TryRefreshIndexInBackgroundAsync()
        {
            try
            {
                await CoRegSvc.RebuildCompanyIndexAsync().ConfigureAwait(false);
            }
            catch { }
        }

        private void PopulateCompanies(IReadOnlyList<CompanyIndexEntry> entries)
        {
            Companies.Clear();
            foreach (var c in entries)
            {
                var name = (c.Name ?? string.Empty).Trim();
                var nameEn = (c.NameEn ?? c.Name ?? string.Empty).Trim();
                Companies.Add(new CompanyItem
                {
                    Id = c.Code ?? nameEn ?? name ?? string.Empty,
					Name = name ?? string.Empty,
					NameEn = nameEn ?? string.Empty,
					Code = c.Code ?? string.Empty
                });
            }
        }

        private void ApplyRecentCompanyOrder(IReadOnlyList<string> recentCodes)
        {
            if (recentCodes.Count == 0 || Companies.Count < 2)
                return;

            var orderedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var orderedCompanies = new List<CompanyItem>(Companies.Count);
            foreach (var code in recentCodes)
            {
                if (!orderedCodes.Add(code))
                    continue;

                var company = Companies.FirstOrDefault(c =>
                    string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
                if (company != null)
                    orderedCompanies.Add(company);
            }

            orderedCompanies.AddRange(Companies.Where(c => !orderedCodes.Contains(c.Code)));
            Companies = new ObservableCollection<CompanyItem>(orderedCompanies);
        }

        private sealed class CompanyIndexEntry
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public string NameEn { get; set; } = "";
            public bool Enabled { get; set; } = true;
        }

        private void OpenSetupWizard()
        {
            var wizard = new StpWiz(StpWizMode.NewCompany, this);
            var accepted = wizard.ShowDialog() == true && wizard.CompletedSuccessfully;
            if (!accepted)
            {
                if (_mode == ComWinMode.Startup)
                {
                    try { WpfApp.Current?.Shutdown(); } catch { }
                }
                return;
            }

            try
            {
                var pref = UsrPref.Load();
                pref.CompanySetupComplete = true;
                pref.Save();
            }
            catch { }

            _ = LoadCompaniesAsync();
        }

        private void BtnSetupWizard_Click(object sender, RoutedEventArgs e)
        {
            if (_mode != ComWinMode.Startup)
                return;

            OpenSetupWizard();
        }

        private void LstCompanies_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_mode == ComWinMode.Management)
                BtnMgmtOpen_Click(sender, e);
            else
                _ = ContinueToLoginAsync();
        }

        private void SafeCloseOrShutdown()
        {
            if (_mode == ComWinMode.Management)
            {
                Close();
                return;
            }

            var hasShell = false;
            try
            {
                foreach (Window w in WpfApp.Current.Windows)
                {
                    if (w is MWin)
                    {
                        hasShell = true;
                        break;
                    }
                }
            }
            catch { }

            if (hasShell)
            {
                Close();
                return;
            }

            try { WpfApp.Current.Shutdown(); } catch { Close(); }
        }

        private async void BtnContinue_Click(object sender, RoutedEventArgs e) => await ContinueToLoginAsync();

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            SafeCloseOrShutdown();
        }

        private async void BtnMgmtOpen_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSelection()) return;

            try
            {
                await SwitchToCompanyAsync(SelectedCompany!);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.Message,
                    Loc.Get("CompanySelection.OpenFailed", "Unable to open company"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async Task SwitchToCompanyAsync(CompanyItem company)
        {
            var shell = WpfApp.Current?.Windows.OfType<MWin>().FirstOrDefault();
            var currentCode = AppState.CurrentCompanyCode;

            if (shell != null && string.Equals(currentCode, company.Code, StringComparison.OrdinalIgnoreCase))
            {
                ShowNotice(Loc.Get("CompanySelection.AlreadyActive", "This company is already active."));
                return;
            }

            if (shell != null)
            {
                var answer = MessageBox.Show(
                    Loc.Get("CompanySelection.SwitchConfirm",
                        "The current company session will be ended and all open company windows and operations will be closed. A new login will then be required for the selected company. The main window will remain open but disabled until sign-in succeeds. Do you want to continue?"),
                    Loc.Get("CompanySelection.SwitchTitle", "Switch Company"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (answer != MessageBoxResult.Yes)
                    return;
            }

            var all = await CoLocalStore.LoadAllAsync();
            var record = all.FirstOrDefault(c =>
                string.Equals(c.Code, company.Code, StringComparison.OrdinalIgnoreCase));

            if (record == null)
                throw new InvalidOperationException(Loc.Get(
                          "CompanySelection.LocalRecordNotFound",
                    "The local company record required to open this company was not found."));

            if (shell != null)
            {
                AppState.InactiveMainWindow = shell;

                try
                {
                    if (shell.DataContext is ViewModels.MainShVM vm)
                        vm.FreezeForCompanySwitch();

                    CloseSecondaryWindowsExceptShell();
                    await AppHost.Auth.LogoutAsync();
                    AppState.CurrentUser = null;

                    ConfigureRuntimeDatabase(record);
                    AppHost.RebindDatabaseContext();
                    AppState.SetCompany(record.Code, record.Name, record.NameEn);
                    var app = WpfApp.Current;
                    if (app != null)
                        app.Properties["SelectedCompany"] = BuildCompanyItem(record);

                    await ShowCompanyLoginAsync(shell);
                }
                catch
                {
                    throw;
                }
            }
            else
            {
                ConfigureRuntimeDatabase(record);
                AppHost.RebindDatabaseContext();
                AppState.SetCompany(record.Code, record.Name, record.NameEn);
                var app = WpfApp.Current;
                if (app != null)
                    app.Properties["SelectedCompany"] = BuildCompanyItem(record);
                await ContinueToLoginAsync();
            }

            Close();
        }

        internal static CompanyItem BuildCompanyItem(StoredCoRec record) => new()
        {
            Id = record.Code,
            Code = record.Code,
            Name = record.Name,
            NameEn = record.NameEn
        };

        internal static Task ShowCompanyLoginAsync(Window shellOwner)
        {
            var login = new LogWin
            {
                Owner = shellOwner,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            login.ShowDialog();
            return Task.CompletedTask;
        }

        private static void ConfigureRuntimeDatabase(StoredCoRec record)
        {
            var settings = AppCfgSvc.Current;
            settings.ConnectionStrings ??= new Dictionary<string, ConnectionStringSettings>(StringComparer.OrdinalIgnoreCase);
            if (!settings.ConnectionStrings.TryGetValue("Default", out var defaultConnection) || defaultConnection == null)
            {
                defaultConnection = new ConnectionStringSettings();
                settings.ConnectionStrings["Default"] = defaultConnection;
            }

            var provider = string.IsNullOrWhiteSpace(record.DatabaseProvider)
                ? DatabaseProviderNames.Sqlite
                : DatabaseProviderNames.Normalize(record.DatabaseProvider);

            defaultConnection.Provider = provider;
            settings.Database ??= new DatabaseSettings();
            settings.Database.DefaultProvider = provider;

            if (DatabaseRuntime.IsSqlite(provider))
            {
                if (string.IsNullOrWhiteSpace(record.DbPath))
                    throw new InvalidOperationException(Loc.Get(
                        "CompanySelection.SqlitePathMissing",
                        "SQLite database path is not configured."));

                defaultConnection.ConnectionString =
                    DatabaseRuntime.NormalizeSqliteConnectionString(record.DbPath);
                AppState.SetDatabase(
                    System.IO.Path.GetFileName(record.DbPath));
                return;
            }

            if (DatabaseRuntime.IsPostgreSql(provider))
            {
                var current = defaultConnection.ConnectionString;
                if (string.IsNullOrWhiteSpace(current))
                    throw new InvalidOperationException(Loc.Get(
                        "CompanySelection.PgConnectionMissing",
                        "Current PostgreSQL connection is not available."));

                var builder = new NpgsqlConnectionStringBuilder(current);
                if (!string.IsNullOrWhiteSpace(record.DbPath))
                {
                    var dbName = Path.GetFileNameWithoutExtension(record.DbPath.TrimEnd('\\', '/'));
                    if (!string.IsNullOrWhiteSpace(dbName))
                        builder.Database = dbName;
                }
                defaultConnection.ConnectionString = builder.ConnectionString;
                return;
            }

            throw new InvalidOperationException(
                Loc.Get("CompanySelection.UnsupportedProvider",
                    "Unsupported database provider.") + $" ({provider})");
        }

        private void CloseSecondaryWindowsExceptShell()
        {
            foreach (Window window in WpfApp.Current.Windows.Cast<Window>().ToList())
            {
                if (ReferenceEquals(window, this) || window is MWin)
                    continue;

                try { window.Close(); } catch { }
            }
        }

        private async void BtnMgmtEdit_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSelection()) return;
            await EnterEditModeAsync(SelectedCompany!);
        }

        private async Task EnterEditModeAsync(CompanyItem item)
        {
            try
            {
                var all = await CoLocalStore.LoadAllAsync();
                var rec = all.FirstOrDefault(c =>
                    string.Equals(c.Code, item.Code, StringComparison.OrdinalIgnoreCase));
                _editingCompanyRecord = rec;

                txtEditName.Text = rec?.Name ?? item.Name ?? string.Empty;
                txtEditNameEn.Text = string.Equals(rec?.NameEn?.Trim(), rec?.Name?.Trim(), StringComparison.Ordinal)
                    ? string.Empty
                    : rec?.NameEn ?? string.Empty;
                txtEditTax.Text = rec?.TaxNumber ?? string.Empty;
                txtEditLicense.Text = rec?.License ?? string.Empty;
                txtEditEmail.Text = rec?.Email ?? string.Empty;
                txtEditWebsite.Text = rec?.Website ?? string.Empty;
                txtEditPhone.Text = rec?.Phone ?? string.Empty;
                txtEditMobile.Text = rec?.Mobile ?? string.Empty;
                txtEditAddress.Text = rec?.AddressLine1 ?? string.Empty;
                txtEditAddress2.Text = rec?.AddressLine2 ?? string.Empty;
                txtEditCity.Text = rec?.City ?? string.Empty;
                txtEditCountry.Text = rec?.Country ?? string.Empty;
                txtEditCurrency.Text = rec?.CurrencyCode ?? string.Empty;
                txtEditDecimalPlaces.Text = rec?.DecimalPlaces.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
                txtEditCalendar.Text = rec?.CalendarType ?? string.Empty;
                txtEditGrouping.Text = rec?.NumberGrouping ?? string.Empty;
                txtEditLanguage.Text = rec?.LanguageCode ?? string.Empty;
                txtEditCoa.Text = rec?.CoaTemplateCode ?? string.Empty;
                txtEditWarehouse.Text = rec?.DefaultWarehouseName ?? string.Empty;
                dpEditFiscalStart.SelectedDate = rec == null || rec.FiscalYearStart == default
                    ? null
                    : rec.FiscalYearStart;
                dpEditFiscalEnd.SelectedDate = rec == null || rec.FiscalYearEnd == default
                    ? null
                    : rec.FiscalYearEnd;
                txtEditNotes.Text = rec?.Notes ?? string.Empty;
                txtEditAdminUser.Text = rec?.AdminUsername ?? string.Empty;
                txtEditAdminName.Text = rec?.AdminFullName ?? string.Empty;
                txtEditDbProvider.Text = rec?.DatabaseProvider ?? string.Empty;
                txtEditDbPath.Text = rec?.DbPath ?? string.Empty;
                txtEditAltDbPath.Text = rec?.AltDbPath ?? string.Empty;
                txtEditBackupPath.Text = rec?.BackupPath ?? string.Empty;
                txtEditAltBackupPath.Text = rec?.AltBackupPath ?? string.Empty;
                txtEditBackupSchedule.Text = rec?.BackupSchedule ?? string.Empty;
                txtEditBackupTime.Text = rec?.BackupTime ?? string.Empty;
                chkEditAutoBackup.IsChecked = rec?.AutoBackup ?? false;

                ApplyEditModeLabels();
                listPanel.Visibility = Visibility.Collapsed;
                editPanel.Visibility = Visibility.Visible;
                editBtnBar.Visibility = Visibility.Visible;
                mgmtPanel.IsEnabled = false;

                txtTitle.Text = Loc.Get("CompanyEdit.Title", "Edit Company Details");
                txtHint.Text = Loc.Get("CompanyEdit.Hint", "Save changes or cancel to return to the list.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message,
                    Loc.Get("Common.Error", "Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ExitEditMode()
        {
            editPanel.Visibility = Visibility.Collapsed;
            listPanel.Visibility = Visibility.Visible;
            editBtnBar.Visibility = Visibility.Collapsed;
            mgmtPanel.IsEnabled = true;
            _editingCompanyRecord = null;
            ApplyLocalizedMgmtLabels();
        }

        private void ApplyEditModeLabels()
        {
            lblEditName.Text = Loc.Get("CompanyEdit.Name", "Name");
            lblEditNameEn.Text = Loc.Get("CompanyEdit.NameEn", "Name (EN)");
            lblEditTax.Text = Loc.Get("CompanyEdit.Tax", "Tax / Registration");
            lblEditLicense.Text = EditLabel("License", "الترخيص");
            lblEditEmail.Text = Loc.Get("CompanyEdit.Email", "Email");
            lblEditWebsite.Text = EditLabel("Website", "الموقع الإلكتروني");
            lblEditPhone.Text = Loc.Get("CompanyEdit.Phone", "Phone");
            lblEditMobile.Text = Loc.Get("CompanyEdit.Mobile", "Mobile");
            lblEditAddress.Text = Loc.Get("CompanyEdit.Address", "Address");
            lblEditAddress2.Text = EditLabel("Address line 2", "العنوان 2");
            lblEditCity.Text = Loc.Get("CompanyEdit.City", "City");
            lblEditCountry.Text = Loc.Get("CompanyEdit.Country", "Country");
            lblEditCurrency.Text = Loc.Get("CompanyEdit.Currency", "Currency");
            lblEditDecimalPlaces.Text = EditLabel("Decimal places", "المنازل العشرية");
            lblEditCalendar.Text = EditLabel("Calendar", "التقويم");
            lblEditGrouping.Text = EditLabel("Number grouping", "تجميع الأرقام");
            lblEditLanguage.Text = EditLabel("Company language", "لغة الشركة");
            lblEditCoa.Text = EditLabel("Chart of accounts", "قالب دليل الحسابات");
            lblEditWarehouse.Text = EditLabel("Default warehouse", "المستودع الافتراضي");
            lblEditFiscalStart.Text = EditLabel("Fiscal year start", "بداية السنة المالية");
            lblEditFiscalEnd.Text = EditLabel("Fiscal year end", "نهاية السنة المالية");
            lblEditNotes.Text = EditLabel("Notes", "ملاحظات");
            lblEditAdminUser.Text = EditLabel("Administrator username", "اسم المستخدم الإداري");
            lblEditAdminName.Text = EditLabel("Administrator name", "اسم المدير");
            lblEditDbProvider.Text = EditLabel("Database provider", "مزود قاعدة البيانات");
            lblEditDbPath.Text = EditLabel("Database path", "مسار قاعدة البيانات");
            lblEditAltDbPath.Text = EditLabel("Alternate database path", "مسار قاعدة البيانات البديل");
            lblEditBackupPath.Text = EditLabel("Backup path", "مسار النسخ الاحتياطي");
            lblEditAltBackupPath.Text = EditLabel("Alternate backup path", "مسار النسخ الاحتياطي البديل");
            lblEditBackupSchedule.Text = EditLabel("Backup schedule", "جدولة النسخ الاحتياطي");
            lblEditBackupTime.Text = EditLabel("Backup time", "وقت النسخ الاحتياطي");
            chkEditAutoBackup.Content = EditLabel("Enable automatic backup", "تفعيل النسخ الاحتياطي التلقائي");
            btnEditSave.Content = Loc.Get("Common.Save", "Save");
            btnEditCancel.Content = Loc.Get("Common.Cancel", "Cancel");
        }

        private static string EditLabel(string english, string arabic)
            => IsArabicUi() ? arabic : english;

        private async void BtnEditSave_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedCompany == null)
            {
                ExitEditMode();
                return;
            }

            var name = (txtEditName.Text ?? string.Empty).Trim();
            var nameEn = (txtEditNameEn.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(nameEn))
            {
                ShowNotice(Loc.Get("CompanyEdit.NameRequired", "Company name is required."));
                return;
            }

            if (!int.TryParse(txtEditDecimalPlaces.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var decimalPlaces)
                || decimalPlaces < 0 || decimalPlaces > 6)
            {
                ShowNotice(EditLabel(
                    "Decimal places must be a number from 0 to 6.",
                    "يجب أن يكون عدد المنازل العشرية رقمًا من 0 إلى 6."));
                return;
            }

            if (dpEditFiscalStart.SelectedDate.HasValue != dpEditFiscalEnd.SelectedDate.HasValue ||
                dpEditFiscalStart.SelectedDate is DateTime fiscalStart &&
                dpEditFiscalEnd.SelectedDate is DateTime fiscalEnd &&
                fiscalEnd.Date < fiscalStart.Date)
            {
                ShowNotice(EditLabel(
                    "Enter both fiscal year dates and make sure the end date is not before the start date.",
                    "أدخل تاريخي السنة المالية مع التأكد من أن تاريخ النهاية ليس قبل تاريخ البداية."));
                return;
            }

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var profile = _editingCompanyRecord ?? new StoredCoRec
                {
                    Code = SelectedCompany.Code,
                    Enabled = true
                };
                profile.Name = name;
                profile.NameEn = nameEn;
                profile.TaxNumber = (txtEditTax.Text ?? string.Empty).Trim();
                profile.License = (txtEditLicense.Text ?? string.Empty).Trim();
                profile.Email = (txtEditEmail.Text ?? string.Empty).Trim();
                profile.Website = (txtEditWebsite.Text ?? string.Empty).Trim();
                profile.Phone = (txtEditPhone.Text ?? string.Empty).Trim();
                profile.Mobile = (txtEditMobile.Text ?? string.Empty).Trim();
                profile.AddressLine1 = (txtEditAddress.Text ?? string.Empty).Trim();
                profile.AddressLine2 = (txtEditAddress2.Text ?? string.Empty).Trim();
                profile.City = (txtEditCity.Text ?? string.Empty).Trim();
                profile.Country = (txtEditCountry.Text ?? string.Empty).Trim();
                profile.CurrencyCode = (txtEditCurrency.Text ?? string.Empty).Trim();
                profile.DecimalPlaces = decimalPlaces;
                profile.CalendarType = (txtEditCalendar.Text ?? string.Empty).Trim();
                profile.NumberGrouping = (txtEditGrouping.Text ?? string.Empty).Trim();
                profile.LanguageCode = (txtEditLanguage.Text ?? string.Empty).Trim();
                profile.CoaTemplateCode = (txtEditCoa.Text ?? string.Empty).Trim();
                profile.DefaultWarehouseName = (txtEditWarehouse.Text ?? string.Empty).Trim();
                profile.FiscalYearStart = dpEditFiscalStart.SelectedDate ?? default;
                profile.FiscalYearEnd = dpEditFiscalEnd.SelectedDate ?? default;
                profile.Notes = (txtEditNotes.Text ?? string.Empty).Trim();
                profile.DbPath = (txtEditDbPath.Text ?? string.Empty).Trim();
                profile.AltDbPath = (txtEditAltDbPath.Text ?? string.Empty).Trim();
                profile.BackupPath = (txtEditBackupPath.Text ?? string.Empty).Trim();
                profile.AltBackupPath = (txtEditAltBackupPath.Text ?? string.Empty).Trim();
                profile.BackupSchedule = (txtEditBackupSchedule.Text ?? string.Empty).Trim();
                profile.BackupTime = (txtEditBackupTime.Text ?? string.Empty).Trim();
                profile.AutoBackup = chkEditAutoBackup.IsChecked == true;

                var databaseUpdated = await CoRegSvc.UpdateCompanyProfileAsync(profile);

                ShowNotice(databaseUpdated
                    ? Loc.Get("CompanyEdit.Saved", "Company details saved.")
                    : EditLabel(
                        "Company details were saved locally, but the database could not be updated.",
                        "تم حفظ بيانات الشركة محليًا، لكن تعذّر تحديث قاعدة البيانات."));
                ExitEditMode();
                await LoadCompaniesAsync();

                SelectedCompany = Companies.FirstOrDefault(c =>
                    string.Equals(c.Code, profile.Code, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message,
                    Loc.Get("Common.Error", "Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void BtnEditCancel_Click(object sender, RoutedEventArgs e)
        {
            ExitEditMode();
        }

        private void BtnMgmtDeleteMenu_Click(object sender, RoutedEventArgs e)
        {
            OpenDeleteMenu();
        }

        private void OpenDeleteMenu()
        {
            if (!EnsureSelection())
                return;

            var menu = btnMgmtDelete.ContextMenu;
            if (menu == null)
                return;

            menu.PlacementTarget = btnMgmtDelete;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private async void BtnDeleteFromIndex_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSelection())
                return;

            try
            {
                await CoLocalStore.RemoveFromIndexAsync(SelectedCompany!.Code);
                ShowNotice(Loc.Get("CompanyManagement.RemovedFromList",
                    "Company removed from list."));
                await LoadCompaniesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message,
                    Loc.Get("Common.Delete", "Delete"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void BtnDeleteFully_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSelection())
                return;

            var confirm = MessageBox.Show(
                Loc.Get("CompanyManagement.ConfirmPermanentDelete",
                    "The selected company will be permanently deleted. This cannot be undone. Continue?"),
                Loc.Get("CompanyManagement.PermanentDeleteTitle", "Permanent Delete"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
                return;

            try
            {
                var ok = await CoRegSvc.DeleteCompanyFullyAsync(SelectedCompany!.Code);
                if (!ok)
                    await CoLocalStore.RemoveFromIndexAsync(SelectedCompany.Code);

                ShowNotice(Loc.Get("CompanyManagement.PermanentlyDeleted",
                    "Company permanently deleted."));
                await LoadCompaniesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message,
                    Loc.Get("CompanyManagement.PermanentDeleteTitle", "Permanent Delete"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void BtnMgmtReindex_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var count = await CoRegSvc.RebuildCompanyIndexAsync();
                await LoadCompaniesAsync();
                ShowNotice($"{Loc.Get("CompanyManagement.ReindexDone", "Company index rebuilt.")} ({count})");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message,
                    Loc.Get("CompanyManagement.Reindex", "Reindex"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void BtnMgmtClose_Click(object sender, RoutedEventArgs e) => Close();

        private bool EnsureSelection()
        {
            if (SelectedCompany != null)
                return true;

            ShowNotice(Loc.Get("CompanySelection.SelectCompanyFirst",
                "Please select a company first."));
            return false;
        }

        private async Task ContinueToLoginAsync()
        {
            if (SelectedCompany == null)
            {
                MessageBox.Show(
                    Loc.Get("CompanySelection.SelectCompanyFirst", "Select a company first."),
                    Loc.Get("Application.Warning", "Warning"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                var all = await CoLocalStore.LoadAllAsync();
                var record = all.FirstOrDefault(c =>
                    string.Equals(c.Code, SelectedCompany.Code, StringComparison.OrdinalIgnoreCase) && c.Enabled);

                if (record == null)
                    throw new InvalidOperationException(Loc.Get(
                        "CompanySelection.LocalRecordNotFound",
                        "The local company record could not be found."));

                await VerifyCompanyDatabaseAsync(record);
                ConfigureRuntimeDatabase(record);
                AppHost.RebindDatabaseContext();

                WpfApp.Current.Properties["SelectedCompany"] = SelectedCompany;
                AppState.SetCompany(record.Code, record.Name, record.NameEn);
                WpfApp.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;

                var loginWindow = new LogWin();
                WpfApp.Current.MainWindow = loginWindow;
                loginWindow.Show();
                loginWindow.Activate();
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"{Loc.Get("CompanySelection.OpenLoginFailed", "Failed to open login")}:\n{ex.Message}",
                    Loc.Get("Application.Error", "Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static async Task VerifyCompanyDatabaseAsync(StoredCoRec record)
        {
            var provider = DatabaseProviderNames.Normalize(record.DatabaseProvider);
            if (DatabaseRuntime.IsSqlite(provider))
            {
                if (string.IsNullOrWhiteSpace(record.DbPath))
                    throw new InvalidOperationException(Loc.Get(
                        "CompanySelection.SqlitePathMissing",
                        "Company database path is not set."));

                var connectionString = DatabaseRuntime.NormalizeSqliteConnectionString(record.DbPath);
                var dataSource = DatabaseRuntime.GetSqliteDataSource(connectionString);
                if (string.IsNullOrWhiteSpace(dataSource) || !File.Exists(dataSource))
                    throw new FileNotFoundException(
                        Loc.Get("CompanySelection.DatabaseFileMissing", "Company database file not found."),
                        dataSource);

                await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='companies'";
                var count = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
                if (count != 1)
                    throw new InvalidDataException(Loc.Get(
                        "CompanySelection.DatabaseNotInitialized",
                        "Company database file is not properly initialized."));
                return;
            }

            if (DatabaseRuntime.IsPostgreSql(provider))
            {
                if (string.IsNullOrWhiteSpace(record.DbPath))
                    throw new InvalidOperationException(Loc.Get(
                        "CompanySelection.PgDatabaseMissing",
                        "PostgreSQL database name is not set."));
                return;
            }

            throw new InvalidOperationException(
                Loc.Get("CompanySelection.UnsupportedProvider",
                    "Unsupported database provider.") + $" ({record.DatabaseProvider})");
        }

        private static void ShowNotice(string english)
        {
            MessageBox.Show(
                english,
                Loc.Get("Common.Notice", "Notice"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class CompanyItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string Code { get; set; } = "";

        public string GetDisplayName(string? languageCode)
        {
            var lang = (languageCode ?? "en").Trim();
            string pick;
            if (lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase))
            {
                pick = FirstNonEmpty(Name, NameEn);
            }
            else
            {
                pick = FirstNonEmpty(NameEn, Name);
            }

            pick = StripLeadingCode(pick);
            if (string.IsNullOrWhiteSpace(pick) || IsMostlyCode(pick))
                return "Unnamed company";
            return pick;
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }
            return string.Empty;
        }

        private static string StripLeadingCode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            var s = value.Trim();
            if (s.Length >= 3 && char.IsDigit(s[0]))
            {
                var i = 0;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == ' '))
                    i++;
                while (i < s.Length && (s[i] == '-' || s[i] == ':' || s[i] == ' '))
                    i++;
                if (i > 0 && i < s.Length)
                    s = s[i..].Trim();
            }
            return s;
        }

        private static bool IsMostlyCode(string value)
        {
            var t = value.Trim();
            if (t.Length == 0) return true;
            return t.All(c => char.IsDigit(c) || char.IsWhiteSpace(c));
        }

        public string DisplayName => GetDisplayName(ShouTech.App.App.CurrentLanguageCode);

        public override string ToString() => DisplayName;
    }
}