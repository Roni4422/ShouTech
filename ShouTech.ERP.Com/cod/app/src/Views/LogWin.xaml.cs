using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ShouTech.App.Localization;
using ShouTech.App.Services;
using ShouTech.App.ViewModels;
using WpfApp = System.Windows.Application;

namespace ShouTech.App.Views
{
    public partial class LogWin : Window
    {
        private readonly LogWinVM _viewModel;
        private bool _syncingPassword;

        public LogWin()
        {
            _viewModel = new LogWinVM();
            _viewModel.LoginSucceeded += OnLoginSucceeded;
            _viewModel.RequestClose += OnRequestClose;
            _viewModel.RequestFocusUsername += FocusUsername;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            InitializeComponent();
            VisuId.ApplyWindow(this);
            SourceInitialized += (_, _) => ApplyCustomTitleBarColor();
            DataContext = _viewModel;
            ApplyWindowDirection();
            ApplyButtonOrder();
            Loaded += (_, _) =>
            {
                ApplyWindowDirection();
                ApplyButtonOrder();
            };

            Loaded += OnLoaded;
            ContentRendered += OnContentRendered;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LogWinVM.Password))
                SyncPasswordBoxFromViewModel();
            else if (e.PropertyName == nameof(LogWinVM.IsPasswordVisible))
                SyncPasswordOnVisibilityChanged();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UiLoczr.ApplyToWindow(this);
            _viewModel.InitializeFromSelectedCompany();
        }

        private void OnContentRendered(object? sender, EventArgs e)
        {
            Activate();
            // Enterprise login UX: password is the primary input on startup.
            // Username remains available for editing, but the caret always starts in password.
            FocusPassword();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _viewModel.LoginCommand.CanExecute(null))
            {
                e.Handled = true;
                _viewModel.LoginCommand.Execute(null);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _viewModel.CancelCommand.Execute(null);
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPassword || sender is not PasswordBox passwordBox)
                return;

            _viewModel.Password = passwordBox.Password;
        }

        private void SyncPasswordBoxFromViewModel()
        {
            if (_viewModel.IsPasswordVisible || txtPassword.Password == _viewModel.Password)
                return;

            _syncingPassword = true;
            txtPassword.Password = _viewModel.Password;
            _syncingPassword = false;
        }

        private void SyncPasswordOnVisibilityChanged()
        {
            if (_viewModel.IsPasswordVisible)
                _viewModel.Password = txtPassword.Password;
            else
                SyncPasswordBoxFromViewModel();
        }

        private void FocusPassword()
        {
            txtPassword.Focus();
            Keyboard.Focus(txtPassword);
        }

        private void FocusUsername()
        {
            txtUsername.Focus();
            Keyboard.Focus(txtUsername);
            txtUsername.SelectAll();
        }

        private async void OnLoginSucceeded()
		{
			try
			{
				// Record the company that was successfully opened.
				await CoLocalStore.MarkAsLastOpenedAsync(
					AppState.CurrentCompanyCode ?? string.Empty);
			}
			catch
			{
				// Failure to update company order must not block login.
			}

			WpfApp.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;

			if (AppState.InactiveMainWindow is MWin existingMain && existingMain.IsVisible)
			{
				AppState.InactiveMainWindow = null;

				if (existingMain.DataContext is MainShVM vm)
				{
					vm.RefreshAfterCompanySession();
					vm.UnfreezeAfterCompanyLogin();
				}

				existingMain.IsEnabled = true;
				existingMain.Activate();
				existingMain.Focus();
				Close();
				return;
			}

			var main = new MWin();
			WpfApp.Current.MainWindow = main;
			main.Show();
			main.Activate();
			Close();
		}

        private void OnRequestClose()
        {
            // During a company handoff, cancellation ends the pending target session.
            // The previous company is NOT restored, and the shell remains frozen.
            // File → Open is the only route to start another company login.
            if (AppState.InactiveMainWindow is MWin shell && shell.IsVisible)
            {
                AppState.CurrentUser = null;
                AppState.ClearCompany();
                try { WpfApp.Current.Properties.Remove("SelectedCompany"); } catch { }

                try { Close(); } catch { }
                shell.Activate();
                shell.Focus();
                return;
            }

            Close();
        }

        private void ApplyWindowDirection()
        {
            try
            {
                LangDirSv.ApplyToWindow(this, ShouTech.App.App.CurrentLanguageCode);
            }
            catch { }
        }

        /// <summary>
        /// Document order OK|Cancel; full-window RTL mirrors the layout for Arabic.
        /// </summary>
        private void ApplyButtonOrder()
        {
            try
            {
                if (btnBar != null)
                {
                    btnBar.ClearValue(FrameworkElement.FlowDirectionProperty);
                    btnBar.HorizontalAlignment = HorizontalAlignment.Right;
                    if (btnOk != null)
                        System.Windows.Controls.Grid.SetColumn(btnOk, 0);
                    if (btnCancel != null)
                        System.Windows.Controls.Grid.SetColumn(btnCancel, 2);
                }
            }
            catch { }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR    = 36;

        private void ApplyCustomTitleBarColor()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                // #607D8B in BGR = 0x8B7D60
                int captionColor = 0x8B7D60;
                int result = DwmSetWindowAttribute(
                    hwnd,
                    DWMWA_CAPTION_COLOR,
                    ref captionColor,
                    sizeof(int));

                if (result != 0)
                    return;

                // White text
                int textColor = 0xFFFFFF;
                result = DwmSetWindowAttribute(
                    hwnd,
                    DWMWA_TEXT_COLOR,
                    ref textColor,
                    sizeof(int));
            }
            catch
            {
            }
        }

    }
}