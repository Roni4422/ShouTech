using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShouTech.App.Common;
using ShouTech.App.Configuration;
using ShouTech.App.Services;

namespace ShouTech.App.Views
{
    public enum StpWizMode
    {
        FirstRun,
        NewCompany
    }

    public partial class StpWiz : Window
    {
        #region Constants and Fields

        private const int MinStep = 1;
        private const int MaxStep = 4;
        private static readonly string[] CalendarTypeKeys = ["Options.Gregorian", "Options.Hijri"];
        private static readonly string[] BackupScheduleKeys = ["Options.Hourly", "Options.Daily", "Options.Weekly", "Options.Monthly"];
        private static readonly string[] LanguageKeys =
        [
            "Options.Arabic", "Options.EnglishUS", "Options.English", "Options.FrenchFR",
            "Options.French", "Options.TurkishTR", "Options.Turkish"
        ];

        private readonly StpWizMode _mode;
        private readonly DispatcherTimer _autoProgressTimer;

        private int _currentStep = MinStep;
        private bool _isSaving;
        private bool _isFinishInProgress;
        private bool _isUserInteracted;
        private bool _isCheckingPasswordAvailability;
        private bool _langUserTouched;
        private bool _syncingLanguageCombo;

        #endregion

        #region Public Properties

        public bool IsSaving => _isSaving;

        public bool CompletedSuccessfully { get; private set; }

        public string? CreatedCompanyCode { get; private set; }

        #endregion

        #region Constructors

        public StpWiz(StpWizMode mode, Window? owner = null)
        {
            _mode = mode;

            InitializeComponent();
            VisuId.ApplyWindow(this);

            ConfigureOwnership(owner);

            SourceInitialized += (_, _) => ApplyCustomTitleBarColor();

            NumberSubstitution.SetSubstitution(
                this,
                NumberSubstitutionMethod.European);

            InitializeWizardUi();
            LoadDefaults();
            SyncLanguageComboWithApp();
            ApplyModeTexts();
            ApplyWizardLocalization();

            _autoProgressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3.0)
            };

            _autoProgressTimer.Tick += AutoProgressTimer_Tick;

            RegisterEventHandlers();
            AppServiceHost.Localization.LanguageChanged += OnApplicationLanguageChanged;
            Closed += (_, _) => AppServiceHost.Localization.LanguageChanged -= OnApplicationLanguageChanged;
            UpdateStepUi();

            Loaded += async (_, __) =>
            {
                ApplyWizardLocalization();

                if (_mode == StpWizMode.NewCompany)
                {
                    await PopulateManagedStoragePathsAsync();
                }
            };

            if (_mode == StpWizMode.FirstRun)
            {
                _autoProgressTimer.Start();
            }

            // Title is applied only by ApplyWizardLocalization (Arabic/English/…).
            // Do not force an English Title here after localization.
        }

        #endregion

        #region Window Configuration

        private void ConfigureOwnership(Window? explicitOwner)
        {
            if (explicitOwner != null &&
                explicitOwner != this &&
                explicitOwner.IsVisible)
            {
                Owner = explicitOwner;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                var fallbackOwner =
                    System.Windows.Application.Current?.MainWindow ??
                    GetActiveWindow();

                if (fallbackOwner != null &&
                    fallbackOwner != this &&
                    fallbackOwner.IsVisible)
                {
                    Owner = fallbackOwner;
                    WindowStartupLocation =
                        WindowStartupLocation.CenterOwner;
                }
                else
                {
                    WindowStartupLocation =
                        WindowStartupLocation.CenterScreen;
                }
            }

            ShowInTaskbar = true;
        }

        private static Window? GetActiveWindow()
        {
            var app = System.Windows.Application.Current;

            if (app == null)
            {
                return null;
            }

            return app.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.IsActive && w.IsVisible);
        }

        #endregion

        #region Native Window Styling

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attr,
            ref int attrValue,
            int attrSize);

        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        private void ApplyCustomTitleBarColor()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;

                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                // #607D8B in BGR format.
                int captionColor = 0x8B7D60;

                var result = DwmSetWindowAttribute(
                    hwnd,
                    DWMWA_CAPTION_COLOR,
                    ref captionColor,
                    sizeof(int));

                if (result != 0)
                {
                    return;
                }

                // Use white title bar text.
                int textColor = 0xFFFFFF;

                result = DwmSetWindowAttribute(
                    hwnd,
                    DWMWA_TEXT_COLOR,
                    ref textColor,
                    sizeof(int));

                if (result != 0)
                {
                    return;
                }
            }
            catch
            {
                // Native title bar styling is optional.
            }
        }

        #endregion

        #region Initialization

        private void InitializeWizardUi()
        {
            UpdateCurrencyOptions();
            cmbCurrency.SelectedIndex = 0;

            LoadCoaTemplatesIntoCombo();

            if (txtDefaultWarehouse != null &&
                string.IsNullOrWhiteSpace(txtDefaultWarehouse.Text))
            {
                txtDefaultWarehouse.Text = "Main Warehouse";
            }

            var currentYear = DateTime.Today.Year;

            dpFiscalStart.SelectedDate =
                new DateTime(currentYear, 1, 1);

            dpFiscalEnd.SelectedDate =
                new DateTime(currentYear, 12, 31);

            UpdateFiscalHint();
        }

        private void UpdateCurrencyOptions()
        {
            var selectedCode = (cmbCurrency.SelectedItem as CurrencyOption)?.Currency.Code;
            cmbCurrency.ItemsSource = ArabCurr.All
                .Select(currency => new CurrencyOption(
                    currency,
                    W($"Currencies.{currency.Code}", currency.Code)))
                .ToList();
            cmbCurrency.DisplayMemberPath = nameof(CurrencyOption.Display);
            if (!string.IsNullOrWhiteSpace(selectedCode))
                cmbCurrency.SelectedItem = cmbCurrency.Items
                    .OfType<CurrencyOption>()
                    .FirstOrDefault(option => option.Currency.Code == selectedCode);
        }

        private sealed record CurrencyOption(ArabCurr Currency, string Display);

        /// <summary>
        /// Defines the available chart of accounts templates.
        /// </summary>
        private sealed class CoaTemplateItem
        {
            public string Code { get; init; } = "";
            public string DisplayKey { get; init; } = "";
            public string Display { get; set; } = "";
            public string FileName { get; init; } = "";
            public string? Country { get; init; }

            public static List<CoaTemplateItem> FromDisk()
            {
                var items = new List<CoaTemplateItem>();
                try
                {
                    foreach (var info in CoaTemplatesSvc.GetAvailableTemplates())
                    {
                        var display = string.IsNullOrWhiteSpace(info.NameEn)
                            ? info.Code
                            : $"{info.Code} — {info.NameEn}";
                        if (!string.IsNullOrWhiteSpace(info.Country))
                            display = $"{display} ({info.Country})";

                        items.Add(new CoaTemplateItem
                        {
                            Code = string.IsNullOrWhiteSpace(info.Code) ? Path.GetFileNameWithoutExtension(info.FileName) : info.Code,
                            DisplayKey = string.IsNullOrWhiteSpace(info.Code) ? info.FileName : $"Coa.{info.Code}",
                            Display = display,
                            FileName = info.FileName,
                            Country = info.Country
                        });
                    }
                }
                catch
                {
                    // Fall through to empty list; a minimal fallback is added below.
                }

                if (items.Count == 0)
                {
                    items.Add(new CoaTemplateItem
                    {
                        Code = "SIMPLE",
                        DisplayKey = "Coa.Simple",
                        Display = "SIMPLE — Simple chart of accounts",
                        FileName = "simple.shc"
                    });
                }

                return items
                    .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        private void LoadCoaTemplatesIntoCombo()
        {
            if (cmbCoaTemplate == null)
                return;

            var items = CoaTemplateItem.FromDisk();
            cmbCoaTemplate.ItemsSource = items;
            cmbCoaTemplate.DisplayMemberPath = nameof(CoaTemplateItem.Display);
            cmbCoaTemplate.SelectedValuePath = nameof(CoaTemplateItem.Code);

            var simple = items.FirstOrDefault(x =>
                string.Equals(x.Code, "SIMPLE", StringComparison.OrdinalIgnoreCase));
            cmbCoaTemplate.SelectedItem = simple ?? (items.Count > 0 ? items[0] : null);
        }

        private void RegisterEventHandlers()
        {
            btnCancel.Click += BtnCancel_Click;
            btnBack.Click += BtnBack_Click;
            btnNext.Click += BtnNext_Click;
            btnFinish.Click += BtnFinish_Click;

            if (cmbLanguageCode != null)
            {
                cmbLanguageCode.SelectionChanged += (_, _) =>
                {
                    if (_syncingLanguageCombo)
                    {
                        return;
                    }

                    _langUserTouched = true;
                    ApplyWizardLocalization();
                };
            }

            dpFiscalStart.SelectedDateChanged +=
                (_, _) => UpdateFiscalHint();

            dpFiscalEnd.SelectedDateChanged +=
                (_, _) => UpdateFiscalHint();

            txtAdminUser.TextChanged +=
                TxtAdminUser_TextChanged;

            txtAdminPassword.PasswordChanged +=
                TxtAdminPassword_PasswordChanged;

            PreviewMouseDown +=
                (_, _) => StopAutoProgressOnUserAction();

            PreviewKeyDown +=
                StpWiz_PreviewKeyDown;
        }

        private void LoadDefaults()
        {
            if (_mode == StpWizMode.FirstRun)
            {
                txtCompanyName.Text = "ShouTech Software Solutions";
                txtCompanyNameEn.Text = "ShouTech Solutions";
                txtTaxNumber.Text = "123456789";
                txtEmail.Text = "info@shoutech.com";
                txtWebsite.Text = "https://shoutech.com";
                txtPhone.Text = "+963111234567";
                txtMobile.Text = "+963944123456";
                txtAddressLine1.Text = "Damascus - Al Midan";
                txtAddressLine2.Text = "Al Midan Corniche Street";
                txtCity.Text = "Damascus";
                txtCountry.Text = "Syria";
                txtLicense.Text = "TR-2026-001";
                txtNotes.Text =
                    "Sample company created by the initial setup wizard.";

                txtAdminUser.Text = string.Empty;
                txtAdminFullName.Text = "System Administrator";
                txtAdminPassword.Password = string.Empty;
                txtAdminPasswordConfirm.Password = string.Empty;

                txtDbPath.Text =
                    @"C:\ShouTech\Data\Database";

                txtAltDbPath.Text =
                    @"C:\ShouTech\Data\Alternate";

                txtBkpPath.Text =
                    @"C:\ShouTech\Backup";

                txtAltBkpPath.Text =
                    @"C:\ShouTech\Backup\Alternate";

                return;
            }

            txtCompanyName.Text = string.Empty;
            txtCompanyNameEn.Text = string.Empty;
            txtTaxNumber.Text = string.Empty;
            txtEmail.Text = string.Empty;
            txtWebsite.Text = string.Empty;
            txtPhone.Text = string.Empty;
            txtMobile.Text = string.Empty;
            txtAddressLine1.Text = string.Empty;
            txtAddressLine2.Text = string.Empty;
            txtCity.Text = string.Empty;
            txtCountry.Text = string.Empty;
            txtLicense.Text = string.Empty;
            txtNotes.Text = string.Empty;

            txtAdminUser.Text = string.Empty;
            txtAdminFullName.Text = string.Empty;
            txtAdminPassword.Password = string.Empty;
            txtAdminPasswordConfirm.Password = string.Empty;

            txtDbPath.Text = string.Empty;
            txtAltDbPath.Text = string.Empty;
            txtBkpPath.Text = string.Empty;
            txtAltBkpPath.Text = string.Empty;

            if (_mode == StpWizMode.NewCompany)
            {
                _ = PopulateManagedStoragePathsAsync();
            }
        }

        #endregion

        #region Language Handling

        private void SyncLanguageComboWithApp()
        {
            try
            {
                if (cmbLanguageCode == null)
                {
                    return;
                }

                var appLang =
                    NormalizeLang(
                        ShouTech.App.App.CurrentLanguageCode);

                ComboBoxItem? best = null;
                ComboBoxItem? fallback = null;

                foreach (var item in
                    cmbLanguageCode.Items.OfType<ComboBoxItem>())
                {
                    var tag =
                        NormalizeLang(item.Tag?.ToString());

                    if (string.IsNullOrEmpty(tag))
                    {
                        continue;
                    }

                    if (tag == appLang)
                    {
                        best = item;
                        break;
                    }

                    if (fallback == null &&
                        tag.StartsWith(
                            appLang,
                            StringComparison.Ordinal))
                    {
                        fallback = item;
                    }
                }

                _syncingLanguageCombo = true;

                try
                {
                    cmbLanguageCode.SelectedItem =
                        best ??
                        fallback ??
                        cmbLanguageCode.Items
                            .OfType<ComboBoxItem>()
                            .FirstOrDefault();
                }
                finally
                {
                    _syncingLanguageCombo = false;
                }

                // Follow the application language until the user
                // explicitly changes the language selector.
                _langUserTouched = false;
            }
            catch
            {
            }
        }

        private static string NormalizeLang(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return "ar";
            }

            code = code
                .Trim()
                .ToLowerInvariant()
                .Replace('_', '-');

            if (code.StartsWith(
                "en",
                StringComparison.Ordinal))
            {
                return "en";
            }

            if (code.StartsWith(
                "fr",
                StringComparison.Ordinal))
            {
                return "fr";
            }

            if (code.StartsWith(
                "tr",
                StringComparison.Ordinal))
            {
                return "tr";
            }

            if (code.StartsWith(
                "ar",
                StringComparison.Ordinal))
            {
                return "ar";
            }

            return code.Split('-')[0];
        }

        private void ApplyModeTexts()
        {
            ApplyWizardLocalization();
        }

        private void OnApplicationLanguageChanged(object? sender, EventArgs e)
        {
            if (_langUserTouched)
                return;

            SyncLanguageComboWithApp();
            ApplyWizardLocalization();
        }

        private string Msg(
            string ar,
            string en,
            string fr,
            string tr)
        {
            return ResolveWizardLanguage() switch
            {
                "en" => en,
                "fr" => fr,
                "tr" => tr,
                _ => ar
            };
        }

        private string ResolveWizardLanguage()
        {
            // The application language is the primary language.
            // The combo box overrides it only after user interaction.
            if (_langUserTouched)
            {
                try
                {
                    var tag =
                        (cmbLanguageCode?.SelectedItem
                            as ComboBoxItem)?.Tag?.ToString();

                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        return NormalizeLang(tag);
                    }
                }
                catch
                {
                }
            }

            try
            {
                return NormalizeLang(
                    ShouTech.App.App.CurrentLanguageCode);
            }
            catch
            {
                return "ar";
            }
        }

        private string GetStepTitle(
            int step,
            string lang)
        {
            var key = step switch
            {
                1 => "CompanyIdentity",
                2 => "CurrencyFormats",
                3 => "AdministratorAccount",
                4 => "StorageBackup",
                _ => "SetupWizard"
            };
            var fallback = step switch
            {
                1 => "Company identity",
                2 => "Currency, formats and fiscal year",
                3 => "Administrator account",
                4 => "Storage and backup",
                _ => "Setup wizard"
            };
            return W($"Steps.{key}", fallback);
        }

        #endregion

        #region Wizard Localization

        private void ApplyWizardLocalization()
        {
            var lang = ResolveWizardLanguage();

            try
            {
                // Central language direction service (RTL for Arabic, LTR otherwise).
                // Sets Window.FlowDirection so the entire visual tree (labels, fields,
                // buttons, chrome content) flips with the language.
                LangDirSv.ApplyToWindow(this, lang);
            }
            catch
            {
            }

            // Keep the root layout flowing with the window (defensive if a parent overrides).
            try
            {
                if (rootLayout != null)
                    rootLayout.FlowDirection = LangDirSv.GetFlowDirection(lang);
            }
            catch
            {
            }

            Title = W(_mode == StpWizMode.NewCompany ? "Title.NewCompany" : "Title.Setup", _mode == StpWizMode.NewCompany ? "New company — ShouTech ERP" : "Company setup — ShouTech ERP");

            txtHeaderTitle.Text = W(_mode == StpWizMode.NewCompany ? "Header.NewCompany" : "Header.Setup", _mode == StpWizMode.NewCompany ? "New company wizard" : "Organization setup wizard");
            btnCancel.Content = W("Buttons.Cancel", "Cancel");
            btnBack.Content = W("Buttons.Back", "Back");
            btnNext.Content = W("Buttons.Next", "Next");

            if (!_isSaving)
            {
                btnFinish.Content = W("Buttons.Finish", "Finish");
            }

            SetText("lblPage2Section", "Company master data" );

            SetText("lblCompanyNameAr", "Company name (local) *" );

            SetText("lblCompanyNameEn", "Company name (English) — optional" );

            SetText("lblTaxNumber", "Tax number — optional" );

            SetText("lblLicense", "Commercial / industrial license — optional" );

            SetText("lblEmail", "Email — optional" );

            SetText("lblWebsite", "Website — optional" );

            SetText("lblAuto8", "Phone — optional" );

            SetText("lblAuto9", "Mobile — optional" );

            SetText("lblAuto10", "Address line 1 — optional" );

            SetText("lblAuto11", "Address line 2 — optional" );

            SetText("lblCity", "City — optional" );

            SetText("lblCountry", "Country — optional" );

            SetText("lblAuto14", "Notes (optional)" );

            SetText("lblAuto15", "Fiscal year" );

            SetText("lblAuto16", "Set the fiscal year start and end dates." );

            SetText("lblAuto17", "From date *" );

            SetText("lblAuto18", "To date *" );

            SetText("lblAuto19", "Currency, calendar & language" );

            SetText("lblAuto20", "Default currency *" );

            SetText("lblAuto21", "Decimal places" );

            SetText("lblAuto22", "Calendar type" );

            SetText("lblAuto23", "Number grouping" );

            SetText("lblAuto24", "Primary UI language" );

            SetText("lblAuto25", "First system administrator" );

            SetText("lblAuto26", "Create the first administrator account for this company." );

            SetText("lblAuto27", "Username *" );

            SetText("lblAuto28", "Full name — optional" );

            SetText("lblAuto29", "Password *" );

            SetText("lblAuto30", "Confirm password *" );

            SetText("lblAuto31", "Storage & backup" );

            SetText("lblAuto32", "Database path (managed) *" );

            SetText("lblAuto33", "Alternate path — optional" );

            SetText("lblAuto34", "Backup path (managed) *" );

            SetText("lblAuto35", "Additional backup — optional" );

            SetText("lblAuto36", "Frequency" );

            SetText("lblAuto37", "Backup time" );

            SetText("lblCoaTemplate", "Chart of accounts template" );
            SetText("lblDefaultWarehouse", "Default warehouse name" );

            if (txtDefaultWarehouse != null)
                txtDefaultWarehouse.ToolTip = W("Fields.DefaultWarehouseHint", "Created automatically with the company");

            SetLocalizedComboItemLabels(cmbCalendarType, CalendarTypeKeys);
            SetLocalizedComboItemLabels(cmbBackupSchedule, BackupScheduleKeys);
            SetLocalizedComboItemLabels(cmbLanguageCode, LanguageKeys);
            UpdateCurrencyOptions();
            if (cmbCoaTemplate?.ItemsSource is System.Collections.IEnumerable source)
            {
                foreach (var item in source)
                {
                    if (item is CoaTemplateItem template)
                        template.Display = W(template.DisplayKey, template.Display);
                }
                cmbCoaTemplate.Items.Refresh();
            }

            try
            {
                if (chkAutoBackup != null)
                {
                    chkAutoBackup.Content = W("Fields.EnableAutoBackup", "Enable automatic backup");
                }
            }
            catch
            {
            }

            UpdateStepTitle(lang);
            UpdateFiscalHint();
            ApplyNavigationButtonOrder(lang);
        }

        private void ApplyNavigationButtonOrder(string lang)
        {
            if (navigationButtons == null)
                return;

            // Force LTR strip so the programmed order is physical left→right.
            // In RTL the strip is aligned to the start edge (physical left in WPF RTL terms = visual start).
            navigationButtons.FlowDirection = FlowDirection.LeftToRight;
            navigationButtons.HorizontalAlignment = LangDirSv.IsRtl(lang)
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
            navigationButtons.Children.Clear();

            // RTL: primary action (Next/Finish) sits at the visual start (right side of strip when strip is LTR-ordered for RTL UX).
            // Order chosen so Cancel stays farthest from the primary action.
            var orderedButtons = LangDirSv.IsRtl(lang)
                ? new UIElement[] { btnFinish, btnNext, btnBack, btnCancel }
                : new UIElement[] { btnCancel, btnBack, btnNext, btnFinish };

            foreach (var button in orderedButtons)
                navigationButtons.Children.Add(button);
        }

        private void UpdateStepTitle(string lang)
        {
            var stepTitle =
                GetStepTitle(_currentStep, lang);

            txtStepTitle.Text = string.Format(
                CultureInfo.CurrentCulture,
                W("Steps.Format", "Step {0} of {1} — {2}"),
                _currentStep,
                MaxStep,
                stepTitle);
        }

        private void SetText(
            string controlName,
            string englishFallback)
        {
            try
            {
                if (FindName(controlName)
                    is not TextBlock textBlock)
                {
                    return;
                }

                textBlock.Text = W($"Fields.{controlName}", englishFallback);
            }
            catch
            {
            }
        }

        private string W(string key, string englishFallback)
            => AppServiceHost.Localization.GetString(
                $"CompanyWizard.{key}",
                ResolveWizardLanguage(),
                englishFallback);

        private void SetLocalizedComboItemLabels(
            ComboBox? combo,
            string[] keys)
        {
            if (combo == null)
                return;
            for (var i = 0; i < combo.Items.Count && i < keys.Length; i++)
            {
                if (combo.Items[i] is ComboBoxItem item)
                    item.Content = W(keys[i], item.Content?.ToString() ?? string.Empty);
            }
        }

        #endregion

        #region Navigation

        private void UpdateStepUi()
        {
            var lang = ResolveWizardLanguage();

            UpdateStepTitle(lang);

            btnBack.IsEnabled =
                _currentStep > MinStep &&
                !_isSaving;

            btnNext.Visibility =
                _currentStep < MaxStep
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            btnFinish.Visibility =
                _currentStep == MaxStep
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            btnNext.IsEnabled = !_isSaving;
            btnFinish.IsEnabled = !_isSaving;

            // Step map after removing welcome page and merging fiscal into currency:
            // 1 = company identity, 2 = currency/formats/fiscal, 3 = admin, 4 = storage
            Page2.Visibility =
                _currentStep == 1
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            PageCurrency.Visibility =
                _currentStep == 2
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            PageAdmin.Visibility =
                _currentStep == 3
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            PageStorage.Visibility =
                _currentStep == 4
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (_currentStep != 3)
            {
                ClearAdminPasswordAvailabilityError();
            }
        }

        private void UpdateFiscalHint()
        {
            if (dpFiscalStart.SelectedDate
                    is not DateTime start ||
                dpFiscalEnd.SelectedDate
                    is not DateTime end)
            {
                txtFiscalHint.Text = string.Empty;
                return;
            }

            var totalDays =
                (end.Date - start.Date).TotalDays + 1;

            var lang = ResolveWizardLanguage();

            if (totalDays > 0)
            {
                txtFiscalHint.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    W("FiscalHint.Length", "Fiscal year length: {0:N0} days"),
                    totalDays);
            }
            else
            {
                txtFiscalHint.Text = W(
                    "FiscalHint.Invalid",
                    "Warning: end date must be after start date.");
            }
        }

        private void StopAutoProgressOnUserAction()
        {
            if (_isUserInteracted)
            {
                return;
            }

            _isUserInteracted = true;

            if (_autoProgressTimer.IsEnabled)
            {
                _autoProgressTimer.Stop();
            }
        }

        #endregion

        #region Event Handlers

        private void AutoProgressTimer_Tick(
            object? sender,
            EventArgs e)
        {
            if (_mode != StpWizMode.FirstRun ||
                _isSaving ||
                _isUserInteracted)
            {
                return;
            }

            if (_currentStep >= MaxStep)
            {
                _autoProgressTimer.Stop();
                return;
            }

            _currentStep++;
            UpdateStepUi();
        }

        private void BtnBack_Click(
            object sender,
            RoutedEventArgs e)
        {
            StopAutoProgressOnUserAction();

            if (_currentStep > MinStep)
            {
                _currentStep--;
                UpdateStepUi();
            }
        }

        private void BtnCancel_Click(
            object sender,
            RoutedEventArgs e)
        {
            StopAutoProgressOnUserAction();

            if (_isSaving)
            {
                return;
            }

            // Close through the normal closing pipeline.
            // FirstRun keeps its exit confirmation.
            Close();
        }

        private void StpWiz_PreviewKeyDown(
            object sender,
            KeyEventArgs e)
        {
            StopAutoProgressOnUserAction();

            if (e.Key == Key.Escape && !_isSaving)
            {
                e.Handled = true;
                Close();
            }
        }

        private async void BtnNext_Click(
            object sender,
            RoutedEventArgs e)
        {
            StopAutoProgressOnUserAction();

            if (!ValidateStep(_currentStep))
            {
                return;
            }

            if (_currentStep == 3 &&
                !await ValidateAdminPasswordAvailabilityAsync())
            {
                txtAdminPassword.Focus();
                return;
            }

            if (_currentStep < MaxStep)
            {
                _currentStep++;

                if (_currentStep == 4 &&
                    _mode == StpWizMode.NewCompany)
                {
                    await PopulateManagedStoragePathsAsync();
                }

                UpdateStepUi();
            }
        }

        private async void BtnFinish_Click(
            object sender,
            RoutedEventArgs e)
        {
            StopAutoProgressOnUserAction();
            await FinishWizardAsync();
        }

        protected override void OnClosing(
            CancelEventArgs e)
        {
            if (_isSaving)
            {
                e.Cancel = true;

                MessageBox.Show(
                    W("Messages.SavingPleaseWait", "Saving company data, please wait a moment."),
                    "ShouTech ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            if (!CompletedSuccessfully &&
                _mode == StpWizMode.FirstRun)
            {
                var response = MessageBox.Show(
                    W("Messages.ExitConfirmBody", "The wizard is not complete yet. Do you want to exit?"),
                    W("Messages.ExitConfirmTitle", "Confirm exit"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (response == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }

            _autoProgressTimer?.Stop();

            base.OnClosing(e);
        }

        #endregion

        #region Validation


        private bool ValidateStep(int stepIndex)
        {
            switch (stepIndex)
            {
                case 1:
                    // Company identity (former step 2).
                    if (string.IsNullOrWhiteSpace(txtCompanyName.Text))
                    {
                        ShowSoftWarning(
                            W("Messages.CompanyNameRequired", "Please enter the company name."),
                            txtCompanyName);
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(txtEmail.Text) &&
                        !IsValidEmail(txtEmail.Text.Trim()))
                    {
                        ShowSoftWarning(
                            W("Messages.InvalidEmail", "Invalid email format."),
                            txtEmail);
                        return false;
                    }
                    break;

                case 2:
                    // Currency / formats / fiscal year (merged).
                    if (cmbCurrency.SelectedItem is not CurrencyOption)
                    {
                        ShowSoftWarning(
                            W("Messages.CurrencyRequired", "Please select the default currency."));
                        return false;
                    }

                    if (dpFiscalStart.SelectedDate is not DateTime start ||
                        dpFiscalEnd.SelectedDate is not DateTime end)
                    {
                        ShowSoftWarning(
                            W("Messages.FiscalDatesRequired",
                              "Please select the fiscal year start and end dates."));
                        return false;
                    }

                    if (end.Date <= start.Date)
                    {
                        ShowSoftWarning(
                            W("Messages.FiscalEndAfterStart",
                              "The end date must be after the start date."));
                        return false;
                    }
                    break;

                case 3:
                    // Administrator account.
                    if (string.IsNullOrWhiteSpace(txtAdminUser.Text))
                    {
                        ShowSoftWarning(
                            W("Messages.UsernameRequired", "Please enter the username."),
                            txtAdminUser);
                        return false;
                    }

                    if (string.IsNullOrEmpty(txtAdminPassword.Password))
                    {
                        ShowSoftWarning(
                            W("Messages.PasswordRequired", "Please enter the password."),
                            txtAdminPassword);
                        return false;
                    }

                    if (!string.Equals(
                            txtAdminPassword.Password,
                            txtAdminPasswordConfirm.Password,
                            StringComparison.Ordinal))
                    {
                        ShowSoftWarning(
                            W("Messages.PasswordMismatch",
                              "Password and confirmation do not match."),
                            txtAdminPasswordConfirm);
                        return false;
                    }
                    break;

                case 4:
                    // Storage paths managed by ShouTech.
                    if (string.IsNullOrWhiteSpace(txtDbPath.Text) ||
                        !IsValidPath(txtDbPath.Text.Trim()))
                    {
                        ShowSoftWarning(
                            W("Messages.DbPathMissing",
                              "ShouTech could not determine the managed database path."),
                            txtDbPath);
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(txtBkpPath.Text) ||
                        !IsValidPath(txtBkpPath.Text.Trim()))
                    {
                        ShowSoftWarning(
                            W("Messages.BackupPathMissing",
                              "ShouTech could not determine the managed backup path."),
                            txtBkpPath);
                        return false;
                    }
                    break;
            }

            return true;
        }


        private bool ValidateAllSteps()
        {
            for (
                var i = MinStep;
                i <= MaxStep;
                i++)
            {
                if (!ValidateStep(i))
                {
                    _currentStep = i;
                    UpdateStepUi();
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region Company Creation

        private async Task FinishWizardAsync()
        {
            if (_isSaving || _isFinishInProgress || CompletedSuccessfully)
            {
                return;
            }

            _isFinishInProgress = true;
            _isSaving = true;

            try
            {
                if (!ValidateAllSteps())
                    return;

                if (!await ValidateAdminPasswordAvailabilityAsync())
                {
                    _currentStep = 3;
                    UpdateStepUi();
                    txtAdminPassword.Focus();
                    return;
                }

                SetBusyState(
                    true,
                    "Creating company...");

                var request = BuildRequest();

                AppHost.Logging.LogInfo(
                    "CompanyCreation",
                    $"Starting company creation: " +
                    $"{request.CompanyNameAr}");

                var result =
                    await AppHost.CompanyCreation
                        .CreateAsync(request);

                if (result.Success)
                {
                    const string op = "CreateCompany";
                    AppHost.Logging.LogInfo(
                        "CompanyCreation",
                        $"[{op}] Success CompanyCode={result.CompanyCode}; SavedToDatabase={result.SavedToDatabase}; SavedLocally={result.SavedLocally}; Window=StpWiz");
                    AppHost.Logging.LogOperation(
                        "CompanyCreation",
                        op,
                        $"Code=APP-3001; Window=StpWiz; CompanyCode={result.CompanyCode}; SavedToDatabase={result.SavedToDatabase}");

                    CompletedSuccessfully = true;
                    CreatedCompanyCode = result.CompanyCode;

                    var successText = string.IsNullOrWhiteSpace(result.Message)
                        ? W("Messages.Success", "Company created successfully.")
                        : result.Message;
                    if (!string.IsNullOrWhiteSpace(result.CompanyCode))
                    {
                        successText =
                            successText +
                            Environment.NewLine +
                            W("Messages.CompanyCodeLabel", "Company code") +
                            ": " +
                            result.CompanyCode;
                    }

                    MessageBox.Show(
                        successText,
                        W("Messages.SuccessTitle", "Company created"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    _isSaving = false;
                    CompleteWizard();
                }
                else
                {
                    const string errorCode = "APP-3002";
                    const string op = "CreateCompany";
                    var detail = string.IsNullOrWhiteSpace(result.Message)
                        ? W("Messages.CreateFailed", "Company was not created.")
                        : result.Message;
                    var logLine =
                        $"Code={errorCode}; Operation={op}; Window=StpWiz; " +
                        $"SavedToDatabase={result.SavedToDatabase}; SavedLocally={result.SavedLocally}; " +
                        $"Detail={detail}";

                    AppHost.Logging.LogWarning("CompanyCreation", logLine);
                    AppHost.Logging.LogOperation("CompanyCreation", op, logLine);
                    AppHost.Logging.LogError(
                        "CompanyCreation",
                        logLine,
                        new InvalidOperationException(detail));

                    MessageBox.Show(
                        $"[{errorCode}] {detail}" +
                        Environment.NewLine + Environment.NewLine +
                        W("Messages.LoggedForReview", "The operation was logged for review.") +
                        Environment.NewLine +
                        $"Window=StpWiz; Operation={op}",
                        W("Messages.CreateFailedTitle", "Company creation failed"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                const string errorCode = "APP-3003";
                const string op = "CreateCompany";
                var logLine =
                    $"Code={errorCode}; Operation={op}; Window=StpWiz; Detail={ex.Message}";
                AppHost.Logging.LogError("CompanyCreation", logLine, ex);
                AppHost.Logging.LogOperation("CompanyCreation", op, logLine);

                MessageBox.Show(
                    $"[{errorCode}] {W("Messages.UnexpectedCreateError", "Company could not be created due to an unexpected error. Details were logged for review.")}" +
                    Environment.NewLine +
                    $"Window=StpWiz; Operation={op}",
                    W("Messages.CreateFailedTitle", "Company creation failed"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetBusyState(false);
                _isFinishInProgress = false;
            }
        }

        /// <summary>
        /// Completes the wizard and opens the company selection window.
        /// </summary>
        private void CompleteWizard()
        {
            if (_mode == StpWizMode.NewCompany)
            {
                DialogResult = true;
                return;
            }

            var companyWindow = new ComWin();

            System.Windows.Application.Current.MainWindow =
                companyWindow;

            companyWindow.Show();

            Close();
        }

        private NewCoReq BuildRequest()
        {
            var currency =
                (cmbCurrency.SelectedItem as CurrencyOption)?.Currency ??
                ArabCurr.All[0];

            var decimalPlaces =
                int.TryParse(
                    (cmbDecimalPlaces.SelectedItem
                        as ComboBoxItem)?
                        .Content?
                        .ToString(),
                    out var dp)
                    ? dp
                    : 2;

            var langCode =
                (cmbLanguageCode.SelectedItem
                    as ComboBoxItem)?
                    .Tag?
                    .ToString()
                ?? "ar";

            var coaCode =
                (cmbCoaTemplate?.SelectedItem
                    as CoaTemplateItem)?
                    .Code
                ?? cmbCoaTemplate?.SelectedValue?.ToString()
                ?? "SIMPLE";

            var warehouseName =
                string.IsNullOrWhiteSpace(
                    txtDefaultWarehouse?.Text)
                    ? "Main Warehouse"
                    : txtDefaultWarehouse.Text.Trim();

            return new NewCoReq
            {
                CompanyNameAr =
                    txtCompanyName.Text.Trim(),

                CompanyNameEn =
                    txtCompanyNameEn.Text.Trim(),

                TaxNumber =
                    txtTaxNumber.Text.Trim(),

                License =
                    txtLicense.Text.Trim(),

                Email =
                    txtEmail.Text.Trim(),

                Website =
                    txtWebsite.Text.Trim(),

                Phone =
                    txtPhone.Text.Trim(),

                Mobile =
                    txtMobile.Text.Trim(),

                AddressLine1 =
                    txtAddressLine1.Text.Trim(),

                AddressLine2 =
                    txtAddressLine2.Text.Trim(),

                City =
                    txtCity.Text.Trim(),

                Country =
                    txtCountry.Text.Trim(),

                Notes =
                    txtNotes.Text.Trim(),

                FiscalYearStart =
                    dpFiscalStart.SelectedDate ??
                    new DateTime(
                        DateTime.Today.Year,
                        1,
                        1),

                FiscalYearEnd =
                    dpFiscalEnd.SelectedDate ??
                    new DateTime(
                        DateTime.Today.Year,
                        12,
                        31),

                CurrencyCode =
                    currency.Code,

                DecimalPlaces =
                    decimalPlaces,

                CalendarType =
                    cmbCalendarType.SelectedIndex == 1
                        ? "Hijri"
                        : "Gregorian",

                NumberGrouping =
                    (cmbNumberGrouping.SelectedItem
                        as ComboBoxItem)?
                        .Content?
                        .ToString()
                    ?? "3-3-3",

                LanguageCode =
                    langCode,

                CoaTemplateCode =
                    coaCode,

                DefaultWarehouseName =
                    warehouseName,

                AdminUsername =
                    txtAdminUser.Text.Trim(),

                AdminPassword =
                    txtAdminPassword.Password,

                AdminFullName =
                    string.IsNullOrWhiteSpace(
                        txtAdminFullName.Text)
                        ? txtAdminUser.Text.Trim()
                        : txtAdminFullName.Text.Trim(),

                DatabaseProvider = "SQLite",

                DbPath =
                    txtDbPath.Text.Trim(),

                AltDbPath =
                    txtAltDbPath.Text.Trim(),

                BackupPath =
                    txtBkpPath.Text.Trim(),

                AltBackupPath =
                    txtAltBkpPath.Text.Trim(),

                AutoBackup =
                    chkAutoBackup.IsChecked == true,

                BackupSchedule =
                    cmbBackupSchedule.SelectedIndex switch
                    {
                        0 => "Hourly",
                        2 => "Weekly",
                        3 => "Monthly",
                        _ => "Daily"
                    },

                BackupTime =
                    txtBackupTime.Text.Trim()
            };
        }

        #endregion

        #region Storage

        private async Task PopulateManagedStoragePathsAsync()
        {
            if (_mode != StpWizMode.NewCompany)
            {
                return;
            }

            try
            {
                var companies =
                    await CoLocalStore
                        .LoadAllAsync()
                        .ConfigureAwait(true);

                var nextNumber =
                    companies
                        .Select(c =>
                            int.TryParse(
                                c.Code,
                                out var n)
                                ? n
                                : 0)
                        .DefaultIfEmpty(0)
                        .Max() + 1;

                var code =
                    nextNumber.ToString(
                        "000",
                        System.Globalization.CultureInfo.InvariantCulture);

                var databaseDirectory =
                    AppPaths.Database;

                var backupDirectory =
                    AppPaths.CompanyBackup(
                        $"ShouTech{code}");

                Directory.CreateDirectory(
                    databaseDirectory);

                Directory.CreateDirectory(
                    backupDirectory);

                txtDbPath.Text =
                    Path.Combine(
                        databaseDirectory,
                        $"ShouTech{code}.db");

                txtBkpPath.Text =
                    backupDirectory;
            }
            catch (Exception ex)
            {
                AppHost.Logging.LogWarning(
                    "CompanyWizard",
                    $"Unable to generate managed storage paths: " +
                    $"{ex.Message}");
            }
        }

        #endregion

        #region Busy State

        private void SetBusyState(
            bool isBusy,
            string statusMessage = "")
        {
            _isSaving = isBusy;

            Mouse.OverrideCursor =
                isBusy
                    ? Cursors.Wait
                    : null;

            btnFinish.Content =
                isBusy
                    ? statusMessage
                    : W("Buttons.Finish", "Finish");

            UpdateStepUi();
        }

        #endregion

        #region Password Validation

        private void ClearPasswordFields()
        {
            txtAdminPassword.Password = string.Empty;
            txtAdminPasswordConfirm.Password = string.Empty;

            ClearAdminPasswordAvailabilityError();
        }

        private async void TxtAdminUser_TextChanged(
            object sender,
            TextChangedEventArgs e)
        {
            if (_currentStep != 3)
            {
                return;
            }

            await ValidateAdminPasswordAvailabilityAsync();
        }

        private async void TxtAdminPassword_PasswordChanged(
            object sender,
            RoutedEventArgs e)
        {
            if (_currentStep != 3)
            {
                return;
            }

            await ValidateAdminPasswordAvailabilityAsync();
        }

        private async Task<bool>
            ValidateAdminPasswordAvailabilityAsync()
        {
            if (_isCheckingPasswordAvailability)
            {
                return string.IsNullOrWhiteSpace(
                    txtAdminPasswordAvailabilityError.Text);
            }

            var username =
                txtAdminUser.Text.Trim();

            var password =
                txtAdminPassword.Password;

            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrEmpty(password))
            {
                ClearAdminPasswordAvailabilityError();
                return true;
            }

            _isCheckingPasswordAvailability = true;

            try
            {
                var owner =
                    await CoLocalStore
                        .FindPasswordOwnerAsync(password);

                if (owner == null ||
                    string.Equals(
                        owner.AdminUsername,
                        username,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ClearAdminPasswordAvailabilityError();
                    return true;
                }

                txtAdminPasswordAvailabilityError.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    W("Messages.PasswordUnavailable", "Password is not available. It is already used by user “{0}”."),
                    owner.AdminUsername);

                txtAdminPasswordAvailabilityError.Visibility =
                    Visibility.Visible;

                return false;
            }
            finally
            {
                _isCheckingPasswordAvailability = false;
            }
        }

        private void ClearAdminPasswordAvailabilityError()
        {
            txtAdminPasswordAvailabilityError.Text =
                string.Empty;

            txtAdminPasswordAvailabilityError.Visibility =
                Visibility.Collapsed;
        }

        #endregion

        #region General Helpers

        /// <summary>
        /// Shows a non-threatening validation message and focuses
        /// the related control when provided.
        /// </summary>
        private static void ShowSoftWarning(
            string message,
            Control? controlToFocus = null)
        {
            MessageBox.Show(
                message,
                "ShouTech ERP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            controlToFocus?.Focus();
        }

        private static bool IsValidEmail(
            string email)
        {
            return Regex.IsMatch(
                email,
                @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
                RegexOptions.IgnoreCase);
        }

        private static bool IsValidPath(
            string path)
        {
            try
            {
                var fullPath =
                    Path.GetFullPath(path);

                return Path.IsPathRooted(fullPath);
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}
