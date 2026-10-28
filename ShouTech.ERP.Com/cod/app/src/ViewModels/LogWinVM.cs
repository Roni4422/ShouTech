using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShouTech.App.Common;
using ShouTech.App.Configuration;
using ShouTech.App.Localization;
using ShouTech.App.Services;
using ShouTech.App.Views;
using WpfApp = System.Windows.Application;

namespace ShouTech.App.ViewModels
{
    public partial class LogWinVM : ObservableObject
    {
        private readonly ILocSvc _localization;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
        private bool _isLoading;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _isStatusError;

        [ObservableProperty]
        private string? _companyName;

        [ObservableProperty]
        private string _windowTitle = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
        private string _username = "admin";

        [ObservableProperty]
        private string _password = string.Empty;

        [ObservableProperty]
        private bool _isPasswordVisible;

        [ObservableProperty]
        private string? _usernameError;

        [ObservableProperty]
        private string? _passwordError;

        [ObservableProperty]
        private string _signInLabel = string.Empty;

        [ObservableProperty]
        private string _cancelLabel = string.Empty;

        [ObservableProperty]
        private string _usernameLabel = string.Empty;

        [ObservableProperty]
        private string _passwordLabel = string.Empty;

        [ObservableProperty]
        private bool _hideUsernameOnNextLaunch;

        [ObservableProperty]
        private string _hideUsernameOptionLabel = string.Empty;

        public Visibility UsernameSectionVisibility =>
            HideUsernameOnNextLaunch
                ? Visibility.Collapsed
                : Visibility.Visible;

        public event Action? LoginSucceeded;
        public event Action? RequestClose;
        public event Action? RequestFocusUsername;
        public event Action? RequestFocusPassword;

        public LogWinVM()
        {
            _localization = AppHost.Localization;
            _localization.LanguageChanged += OnLanguageChanged;

            RefreshLocalizedStrings();
        }

        public void InitializeFromSelectedCompany()
        {
            if (WpfApp.Current.Properties["SelectedCompany"] is CompanyItem company)
            {
                CompanyName = company.Name;

                WindowTitle = AppFormat.Format(
                    Loc.Get("Login.WindowTitleWithCompany"),
                    company.Name);
            }

            var prefs = UsrPref.Load();

            if (!string.IsNullOrWhiteSpace(prefs.RememberedUsername))
            {
                Username = prefs.RememberedUsername.Trim();
            }

            HideUsernameOnNextLaunch = prefs.HideUsernameOnNextLaunch;

            if (HideUsernameOnNextLaunch &&
                string.IsNullOrWhiteSpace(Username))
            {
                HideUsernameOnNextLaunch = false;
            }
        }

        partial void OnIsPasswordVisibleChanged(bool value)
        {
            OnPropertyChanged(nameof(PasswordFieldVisibility));
            OnPropertyChanged(nameof(PasswordTextVisibility));
        }

        partial void OnHideUsernameOnNextLaunchChanged(bool value)
        {
            OnPropertyChanged(nameof(UsernameSectionVisibility));

            var prefs = UsrPref.Load();

            prefs.HideUsernameOnNextLaunch = value;

            if (value && !string.IsNullOrWhiteSpace(Username))
            {
                prefs.RememberedUsername = Username.Trim();
            }

            prefs.Save();
        }

        public Visibility PasswordFieldVisibility =>
            IsPasswordVisible
                ? Visibility.Collapsed
                : Visibility.Visible;

        public Visibility PasswordTextVisibility =>
            IsPasswordVisible
                ? Visibility.Visible
                : Visibility.Collapsed;

        [RelayCommand(CanExecute = nameof(CanLogin))]
        private async Task LoginAsync()
        {
            if (!ValidateInputs())
                return;

            IsLoading = true;
            IsStatusError = false;
            StatusMessage = Loc.Get("Login.Verifying");

            ClearFieldErrors();

            var user = Username.Trim();
            var pass = Password;

            try
            {
                await Task.Delay(600);

                var result = await AppHost.Auth.LoginAsync(user, pass);

                if (result.Success && result.User != null)
                {
                    StatusMessage = Loc.Get("Login.Success");
                    IsStatusError = false;

                    var prefs = UsrPref.Load();

                    prefs.RememberedUsername = user;
                    prefs.HideUsernameOnNextLaunch =
                        HideUsernameOnNextLaunch;

                    prefs.Save();

                    await Task.Delay(300);

                    LoginSucceeded?.Invoke();
                }
                else
                {
                    StatusMessage = Loc.Get("Login.InvalidCredentials");
                    IsStatusError = true;

                    Password = string.Empty;

                    RequestFocusPassword?.Invoke();
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private void Cancel()
        {
            RequestClose?.Invoke();
        }

        private bool CanLogin()
        {
            return !IsLoading;
        }

        private bool ValidateInputs()
        {
            ClearFieldErrors();

            IsStatusError = false;
            StatusMessage = Loc.Get("Login.DefaultStatus");

            var isValid = true;

            if (HideUsernameOnNextLaunch &&
                string.IsNullOrWhiteSpace(Username))
            {
                HideUsernameOnNextLaunch = false;
            }

            if (string.IsNullOrWhiteSpace(Username))
            {
                UsernameError = Loc.Get("Login.UsernameRequired");
                isValid = false;
            }

            if (string.IsNullOrEmpty(Password))
            {
                PasswordError = Loc.Get("Login.PasswordRequired");
                isValid = false;
            }

            if (!isValid)
            {
                StatusMessage = Loc.Get("Login.FixFields");
                IsStatusError = true;

                if (!string.IsNullOrWhiteSpace(UsernameError))
                {
                    RequestFocusUsername?.Invoke();
                }
                else if (!string.IsNullOrWhiteSpace(PasswordError))
                {
                    RequestFocusPassword?.Invoke();
                }
            }

            return isValid;
        }

        private void ClearFieldErrors()
        {
            UsernameError = null;
            PasswordError = null;
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            RefreshLocalizedStrings();
        }

        private void RefreshLocalizedStrings()
        {
            SignInLabel = Loc.Get("Login.SignIn");
            CancelLabel = Loc.Get("Login.Cancel");
            UsernameLabel = Loc.Get("Login.Username");
            PasswordLabel = Loc.Get("Login.Password");
            HideUsernameOptionLabel =
                Loc.Get("HideUsernameNextTime");

            StatusMessage = Loc.Get("Login.DefaultStatus");

            WindowTitle = string.IsNullOrWhiteSpace(CompanyName)
                ? Loc.Get("Login.WindowTitle")
                : AppFormat.Format(
                    Loc.Get("Login.WindowTitleWithCompany"),
                    CompanyName);
        }
    }
}