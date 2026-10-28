using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using ShouTech.App.Common;
using ShouTech.App.Localization;
using ShouTech.App.Navigation;
using ShouTech.App.Services;
using ShouTech.App.Views;

namespace ShouTech.App.ViewModels
{
    /// <summary>
    /// Main shell ViewModel — orchestrates navigation, visual identity,
    /// status bar, localization, and system actions.
    /// </summary>
    public sealed class MainShVM : BaseVM, IDisposable
    {
        private readonly INavServ _navigation;
        private readonly VisuId _visual;
        private readonly ILogServ _logging;
        private readonly IBkpServ _backup;
        private readonly IDataServ _data;
        private readonly ILocSvc _localization;
        private readonly DispatcherTimer _clockTimer;
        private readonly Window? _ownerWindow;

        private object? _currentContent;
        private string _statusMessage = string.Empty;
        private string _currentTime = AppFormat.FormatTime(DateTime.Now);
        private string _currentUser = string.Empty;
        private string _currentUsername = string.Empty;
        private string _userRoleLabel = string.Empty;
        private UsrAccLvl _userAccessLevel = UsrAccLvl.User;
        private bool _isReadOnlyUser;
        private string _companyName = string.Empty;
        private string _titleCompanyName = string.Empty;
		private string _currentDatabase = string.Empty;
        private string _windowTitle = string.Empty;
        private string _versionLabel = string.Empty;
        private bool _isReady = true;
        private string _bottomUserDisplay = string.Empty;

        #region Properties

        public object? CurrentContent
        {
            get => _currentContent;
            private set => SetProperty(ref _currentContent, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public string CurrentTime
        {
            get => _currentTime;
            private set => SetProperty(ref _currentTime, value);
        }

        public string CurrentUser
        {
            get => _currentUser;
            private set => SetProperty(ref _currentUser, value);
        }

        public string CurrentUsername
        {
            get => _currentUsername;
            private set => SetProperty(ref _currentUsername, value);
        }

        public string UserRoleLabel
        {
            get => _userRoleLabel;
            private set => SetProperty(ref _userRoleLabel, value);
        }

        public UsrAccLvl UsrAccLvl
        {
            get => _userAccessLevel;
            private set
            {
                if (SetProperty(ref _userAccessLevel, value))
                {
                    OnPropertyChanged(nameof(IsAdministratorUser));
                    OnPropertyChanged(nameof(IsViewerUser));
                }
            }
        }

        public bool IsReadOnlyUser
        {
            get => _isReadOnlyUser;
            private set => SetProperty(ref _isReadOnlyUser, value);
        }

        public bool IsAdministratorUser =>
            UsrAccLvl == UsrAccLvl.Administrator || UsrSession.IsAdministrator;

        public bool IsViewerUser =>
            UsrAccLvl == UsrAccLvl.Viewer || UsrSession.IsViewer;

        public string BottomUserDisplay
        {
            get => _bottomUserDisplay;
            private set => SetProperty(ref _bottomUserDisplay, value);
        }

        public string CompanyName
        {
            get => _companyName;
            private set => SetProperty(ref _companyName, value);
        }
		
		public string CurrentDatabase
		{
			get => _currentDatabase;
			private set => SetProperty(ref _currentDatabase, value);
		}

        public string WindowTitle
        {
            get => _windowTitle;
            private set => SetProperty(ref _windowTitle, value);
        }

        public string VersionLabel
        {
            get => _versionLabel;
            private set => SetProperty(ref _versionLabel, value);
        }

        public bool IsReady
        {
            get => _isReady;
            private set => SetProperty(ref _isReady, value);
        }

        public ObservableCollection<DockedWinItem> DockedWindows { get; } = new();

        // Events used by MWin.
        public event EventHandler? RequestOpenMaterialCard;
        public event EventHandler? RequestOpenMaterialGroups;
        public event EventHandler? RequestOpenMaterialDirectory;

        public void RaiseRequestOpenMaterialCard() =>
            RequestOpenMaterialCard?.Invoke(this, EventArgs.Empty);

        public void RaiseRequestOpenMaterialGroups() =>
            RequestOpenMaterialGroups?.Invoke(this, EventArgs.Empty);

        public void RaiseRequestOpenMaterialDirectory() =>
            RequestOpenMaterialDirectory?.Invoke(this, EventArgs.Empty);

        public void LayoutDockedWindows(double containerWidth)
        {
            try
            {
                var x = 8.0;

                foreach (var item in DockedWindows)
                {
                    item.Left = x;
                    x += item.ItemWidth + 6;
                }
            }
            catch
            {
            }
        }

        #endregion

        #region Commands

        public ICommand ShowDashboardCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }
        public ICommand PrintCommand { get; }
        public ICommand FindCommand { get; }
        public ICommand SettingsCommand { get; }
        public ICommand ReportsCommand { get; }
        public ICommand ImportCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand RestoreDockedWindowCommand { get; }

        #endregion

        #region Constructors

        /// <summary>
        /// Default constructor kept for compatibility with existing code.
        /// </summary>
        public MainShVM() : this(null)
        {
        }

        /// <summary>
        /// Main constructor receiving the MWin owner window.
        /// </summary>
        /// <param name="ownerWindow">Main MWin window.</param>
        public MainShVM(Window? ownerWindow)
        {
            _ownerWindow = ownerWindow;

            _navigation = AppHost.Navigation;
            _visual = AppHost.Visual;
            _logging = AppHost.Logging;
            _backup = AppHost.Backup;
            _data = AppHost.Data;
            _localization = AppHost.Localization;

            ShowDashboardCommand = new RelayCommand(ShowDashboard);

            SaveCommand = new RelayCommand(
                () => StatusMessage = StatBarEn.Save);

            UndoCommand = new RelayCommand(
                () => StatusMessage = StatBarEn.Undo);

            RedoCommand = new RelayCommand(
                () => StatusMessage = StatBarEn.Redo);

            PrintCommand = new RelayCommand(
                () => StatusMessage = StatBarEn.Print);

            FindCommand = new RelayCommand(
                () => StatusMessage = StatBarEn.Search);

            SettingsCommand = new RelayCommand(
                () => NavigateToModule(
                    Loc.Get("Tools.Options"),
                    "Tools"));

            ReportsCommand = new RelayCommand(
                () => NavigateToModule(
                    Loc.Get("Reports.Statistics"),
                    "Reports"));

            ImportCommand = new RelayCommand(
                () => NavigateToModule(
                    Loc.Get("Menu.Import"),
                    "File"));

            ExportCommand = new RelayCommand(
                () => NavigateToModule(
                    Loc.Get("Menu.Export"),
                    "File"));

            ExitCommand = new RelayCommand(ExecuteExit);

            RestoreDockedWindowCommand =
                new RelayCommand<DockedWinItem>(RestoreDockedWindow);

            RefreshLocalizedStrings();

            _localization.LanguageChanged += OnLanguageChanged;

            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            _clockTimer.Tick += (_, _) =>
                CurrentTime = AppFormat.FormatTime(DateTime.Now);

            _clockTimer.Start();

            if (UsrSession.Current != null)
                ApplyUsrSession(UsrSession.Current);

            ShowDashboard();

            _ = LoadSessionAsync();
        }

        #endregion

        #region Window Management

        public void DockWindow(string title, Window window)
        {
            foreach (var existing in DockedWindows)
            {
                if (ReferenceEquals(existing.Window, window))
                    return;
            }

            DockedWindows.Add(
                new DockedWinItem
                {
                    Title = title,
                    Window = window
                });
        }

        public void UndockWindow(DockedWinItem item) =>
            DockedWindows.Remove(item);

        public void RemoveDockedWindow(Window window)
        {
            for (var i = DockedWindows.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(DockedWindows[i].Window, window))
                    DockedWindows.RemoveAt(i);
            }
        }

        private void RestoreDockedWindow(DockedWinItem? item)
        {
            if (item == null)
                return;

            ShellWinDock.Restore(item, this);
        }

        #endregion

        #region Navigation & Ribbon Actions

        public void HandleRibbonAction(
            string? label,
            string? actionKey,
            string? tabName,
            string? tabHeader)
        {
            if (string.IsNullOrWhiteSpace(label) &&
                string.IsNullOrWhiteSpace(actionKey))
            {
                return;
            }

            if (TryHandleSystemAction(actionKey, label))
                return;

            var module = RibModMap.ResolveModule(tabName, tabHeader);

            NavigateToModule(
                label ?? actionKey ?? string.Empty,
                module);
        }

        public void ShowDashboard()
        {
            CurrentContent = _navigation.NavigateTo("dashboard");
            StatusMessage = StatBarEn.Dashboard;
        }

        private void NavigateToModule(string label, string module)
        {
            CurrentContent =
                _navigation.NavigateToModule(label, module);

            StatusMessage =
                StatBarEn.ModuleName(module);
        }

        #endregion

        #region System Actions

        private bool TryHandleSystemAction(
            string? actionKey,
            string? label)
        {
            var key = actionKey ?? label ?? string.Empty;

            switch (key)
            {
                case "Menu.Open":
                case "فتح":
                    OpenCompanyWindow();
                    return true;

                case "Menu.New":
                case "جديد":
                    OpenNewCompanyWizard();
                    return true;

                case "Menu.Exit":
                case "Exit.Exit":
                case "خروج":
                    ExecuteExit();
                    return true;

                case "Exit.ExitAndShutdown":
                case "خروج وإطفاء الحاسب":
                    var result = MessageBox.Show(
                        Loc.Get("Messages.ConfirmExit"),
                        Loc.Get("Buttons.OK"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        ExecuteExit();

                        try
                        {
                            System.Diagnostics.Process.Start(
                                "shutdown",
                                "/s /t 0");
                        }
                        catch
                        {
                        }
                    }

                    return true;

                case "Help.About":
                case "حول البرنامج":
                    MessageBox.Show(
                        $"{Loc.Get("Application.Title")}\n" +
                        $"{Loc.Get("Shell.VersionLabel")}\n" +
                        "© 2026 ShouTech Software\n\n" +
                        $"{Loc.Get("Shell.Tagline")}",
                        Loc.Get("Help.About"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    StatusMessage = StatBarEn.About;
                    return true;

                case "Tools.Backup":
                case "النسخ الاحتياطي":
                    try
                    {
                        StatusMessage = StatBarEn.Loading;

                        var backupResult =
                            _backup.PerformRotatedBackup();

                        StatusMessage = backupResult.Success
                            ? StatBarEn.SaveSuccess
                            : StatBarEn.OperationFailed;
                    }
                    catch (Exception ex)
                    {
                        StatusMessage =
                            StatBarEn.OperationFailed;

                        _logging.LogError(
                            "Backup",
                            "Ribbon backup failed",
                            ex);
                    }

                    return true;
            }

            return false;
        }

        #endregion

        #region Language Management

        private void RefreshLocalizedStrings()
        {
            // Refresh role text according to the current language.
            if (UsrAccLvl != UsrAccLvl.User ||
                !string.IsNullOrWhiteSpace(CurrentUsername))
            {
                UserRoleLabel =
                    ResolveRoleLabel(UsrAccLvl);
            }

            // Use the active company from AppState.
            var companyName =
                AppState.CurrentCompanyDisplayName;

            if (string.IsNullOrWhiteSpace(companyName))
            {
                companyName = _titleCompanyName;
            }

            _titleCompanyName =
                companyName ?? string.Empty;

            WindowTitle =
                string.IsNullOrWhiteSpace(_titleCompanyName)
                    ? $"{Loc.Get("Application.Title")} — {UserRoleLabel}"
                    : $"{Loc.Get("Application.Title")} — " +
                      $"{_titleCompanyName} — {UserRoleLabel}";

            RefreshStatusBar();
        }

        private void OnLanguageChanged(
            object? sender,
            EventArgs e)
        {
            RefreshLocalizedStrings();

            _navigation.ClearCache();

            ShowDashboard();

            RefreshStatusBar();

            UiLoczr.ScheduleApplyToAllWindows();
        }

        private void RefreshStatusBar()
		{
			VersionLabel = "📊 " + StatBarEn.Version;

			CompanyName = AppState.CurrentCompanyDisplayName;

			if (string.IsNullOrWhiteSpace(CompanyName))
			{
				CompanyName = _titleCompanyName;
			}

			CurrentDatabase = AppState.CurrentDatabaseName ?? string.Empty;

			if (!string.IsNullOrWhiteSpace(CurrentUsername))
			{
				BottomUserDisplay =
					StatBarEn.BuildUserDisplay(
						CurrentUsername,
						CurrentUser,
						UsrAccLvl);
			}

			if (IsReady)
			{
				StatusMessage =
					IsReadOnlyUser
						? StatBarEn.ViewerMode
						: StatBarEn.Ready;
			}
		}

        #endregion

        #region User Session

        private async Task LoadSessionAsync()
        {
            try
            {
                IsReady = false;
                StatusMessage = StatBarEn.Loading;

                var user =
                    await AppHost.Auth.GetCurrentUserAsync();

                if (user != null)
                {
                    ApplyUsrSession(user);
                }
                else if (AppState.CurrentUser != null)
                {
                    ApplyUsrSession(
                        MapFromAppState(AppState.CurrentUser));
                }

                RefreshStatusBar();

                IsReady = true;
            }
            catch (Exception ex)
            {
                _logging.LogError(
                    "MainShell",
                    "LoadSessionAsync",
                    ex);

                StatusMessage = StatBarEn.Ready;
                IsReady = true;
            }
        }

        private void ApplyUsrSession(
            Services.UserInfo user)
        {
            CurrentUser =
                user.FullName ?? string.Empty;

            CurrentUsername =
                user.Username ?? string.Empty;

            UsrAccLvl =
                user.AccessLevel;

            IsReadOnlyUser =
                user.IsReadOnly;

            UserRoleLabel =
                ResolveRoleLabel(
                    user.AccessLevel);

            // Use the currently selected company from AppState.
            _titleCompanyName =
                AppState.CurrentCompanyDisplayName;

            // Fallback only if AppState has no active company.
            if (string.IsNullOrWhiteSpace(
                    _titleCompanyName))
            {
                _titleCompanyName =
                    user.CompanyName ?? string.Empty;
            }

            CompanyName =
                _titleCompanyName;

            WindowTitle =
                string.IsNullOrWhiteSpace(
                    _titleCompanyName)
                    ? $"{Loc.Get("Application.Title")} — " +
                      $"{UserRoleLabel}"
                    : $"{Loc.Get("Application.Title")} — " +
                      $"{_titleCompanyName} — " +
                      $"{UserRoleLabel}";

            RefreshStatusBar();

            if (user.IsReadOnly)
            {
                StatusMessage =
                    StatBarEn.ViewerMode;
            }
        }

        private static string ResolveRoleLabel(
            UsrAccLvl accessLevel) =>
            accessLevel switch
            {
                UsrAccLvl.Administrator =>
                    AppHost.Localization.GetString(
                        "Shell.RoleAdministrator"),

                UsrAccLvl.Viewer =>
                    AppHost.Localization.GetString(
                        "Shell.RoleViewer"),

                _ =>
                    AppHost.Localization.GetString(
                        "Shell.RoleUser")
            };

        private static Services.UserInfo MapFromAppState(
            Services.UserInfo user) =>
            new()
            {
                Username =
                    user.Username ?? string.Empty,

                FullName =
                    user.FullName ?? string.Empty,

                Role =
                    user.Role ?? string.Empty,

                RoleCode =
                    user.RoleCode ?? string.Empty,

                AccessLevel =
                    user.AccessLevel,

                IsSuperAdmin =
                    user.IsSuperAdmin,

                IsAuthenticated =
                    user.IsAuthenticated,

                CompanyName =
                    AppState.CurrentCompanyDisplayName
            };

        #endregion

        #region Company Creation

        /// <summary>
        /// Opens the new-company wizard using MWin as owner.
        /// </summary>
        private void OpenNewCompanyWizard()
        {
            var wizard =
                new StpWiz(
                    StpWizMode.NewCompany,
                    _ownerWindow);

            wizard.Closed += (_, _) =>
            {
                if (wizard.CompletedSuccessfully)
                {
                    StatusMessage =
                        StatBarEn.SaveSuccess;
                }
            };

            wizard.Show();
        }

        #endregion

        #region Exit & Disposal

        private static void ExecuteExit()
        {
            System.Windows.Application.Current.Shutdown();
        }

        public void Dispose()
        {
            _clockTimer.Stop();

            _localization.LanguageChanged -=
                OnLanguageChanged;
        }

        #endregion

        #region Company Switch Freeze / Unfreeze

        private bool _isFrozen;

        public void FreezeForCompanySwitch()
        {
            if (_isFrozen)
                return;

            _isFrozen = true;

            try
            {
                StatusMessage =
                    "Switching company...";
            }
            catch
            {
            }
        }

        public void UnfreezeAfterCompanyLogin()
        {
            if (!_isFrozen)
                return;

            _isFrozen = false;

            try
            {
                StatusMessage = "Ready";
            }
            catch
            {
            }
        }

        public void RefreshAfterCompanySession()
		{
			// Refresh the authenticated user from the current company session.
			if (AppState.CurrentUser is Services.UserInfo user)
			{
				ApplyUsrSession(user);
			}
			else
			{
				CurrentUser = string.Empty;
				CurrentUsername = string.Empty;
				UsrAccLvl = UsrAccLvl.User;
				IsReadOnlyUser = false;
			}

			// Refresh the active company.
			_titleCompanyName =
				AppState.CurrentCompanyDisplayName;

			CompanyName =
				_titleCompanyName;

			// Refresh the active database.
			CurrentDatabase =
				AppState.CurrentDatabaseName ?? string.Empty;

			// Refresh all localized/status information.
			RefreshLocalizedStrings();
			RefreshStatusBar();

			// Rebuild the dashboard for the newly selected company.
			ShowDashboard();

			OnPropertyChanged(nameof(CurrentUser));
			OnPropertyChanged(nameof(CurrentUsername));
			OnPropertyChanged(nameof(UsrAccLvl));
			OnPropertyChanged(nameof(CompanyName));
			OnPropertyChanged(nameof(CurrentDatabase));
			OnPropertyChanged(nameof(WindowTitle));
		}
        #endregion

        #region Company Window

        private static void OpenCompanyWindow()
        {
            try
            {
                var existing =
                    System.Windows.Application.Current.Windows
                        .OfType<Views.ComWin>()
                        .FirstOrDefault(
                            w => w.IsVisible);

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

                var win =
                    new Views.ComWin(
                        Views.ComWinMode.Management)
                    {
                        Owner =
                            System.Windows.Application.Current
                                .MainWindow,

                        WindowStartupLocation =
                            WindowStartupLocation.CenterOwner
                    };

                win.Show();
                win.Activate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"تعذر فتح نافذة الشركات: {ex.Message}",
                    "ShouTech ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        #endregion
    }
}