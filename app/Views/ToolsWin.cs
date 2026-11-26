using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;

namespace ShouTech.App.Views;

/// <summary>
/// Unified Tools window (TOOLS_WINDOW_STUDY): one home for the program's
/// tools, in the program's own OptWin idiom (category rail + section
/// bodies + bottom action bar). Two sections carry real content of their
/// own — Overview (health/status derived live from the real stores) and
/// Backup &amp; automation (the AppConfig backup/cycle settings whose
/// consumers are BkpServ.IsBackupDue / PruneOldBackups and the
/// OnlineCoordinator cycle; they had no UI before this window). Every
/// other tool stays in its own window and is LAUNCHED from here through
/// the very same routes the ribbon uses (RibbonNav.TryHandle, or direct
/// construction exactly where the shell does that today) — no logic is
/// duplicated. Settings without a consumer are deliberately NOT shown;
/// the study's gap list records them with reasons.
/// </summary>
public sealed class ToolsWin : BaseWin
{
    protected override string? ScheduleTintName => "Hickory";

    // One catalog drives the launch cards AND the Overview search, so
    // the two can never disagree about what a tool is or where it lives.
    private sealed record ToolEntry(
        string NameKey, string DescKey, string Keywords, Action<ToolsWin> Launch);

    private static ToolEntry Ribbon(string nameKey, string descKey, string keywords, string ribbonKey)
        => new(nameKey, descKey, keywords, self => self.LaunchRibbon(ribbonKey));

    private static readonly ToolEntry[] Catalog =
    {
        // Maintenance
        Ribbon("ToolsWin.CardMaintenance", "ToolsWin.CardMaintenanceDesc", "maintenance health vacuum integrity recalculate costs trial balance", "Tools.MaintenanceWizard"),
        Ribbon("ToolsWin.CardCompress", "ToolsWin.CardCompressDesc", "compress invoices delete drafts purge", "Invoices.CompressInvoices"),
        Ribbon("ToolsWin.CardRepost", "ToolsWin.CardRepostDesc", "repost totals recompute documents manufacturing", "Invoices.RepostInvoices"),
        Ribbon("ToolsWin.CardRollover", "ToolsWin.CardRolloverDesc", "rollover fiscal year close open year", "OtherTools.Rotation"),
        // Data
        new("ToolsWin.CardExport", "ToolsWin.CardExportDesc", "export data exchange files", self => self.LaunchWindow(new DatWin(true))),
        new("ToolsWin.CardImport", "ToolsWin.CardImportDesc", "import data exchange files", self => self.LaunchWindow(new DatWin(false))),
        new("ToolsWin.CardBin", "ToolsWin.CardBinDesc", "recycle bin restore delete purged documents", self => self.LaunchWindow(new BinWin())),
        Ribbon("ToolsWin.CardRestoreCompany", "ToolsWin.CardRestoreCompanyDesc", "restore deleted company", "OtherTools.RestoreCompany.Restore"),
        Ribbon("ToolsWin.CardSync", "ToolsWin.CardSyncDesc", "pos outbox sync messages retry failed", "POS.SyncPOS"),
        // Design & printing
        Ribbon("ToolsWin.CardReports", "ToolsWin.CardReportsDesc", "report designer custom reports", "Reports.CustomizeReports"),
        Ribbon("ToolsWin.CardInvoiceDesigner", "ToolsWin.CardInvoiceDesignerDesc", "invoice designer voucher layout design", "Invoices.InvoiceDesigner"),
        new("ToolsWin.CardBarcodeDesigner", "ToolsWin.CardBarcodeDesignerDesc", "barcode label designer", self => RptNav.OpenBarcodeDesigner(self)),
        new("ToolsWin.CardBarcodeGenerator", "ToolsWin.CardBarcodeGeneratorDesc", "barcode generator print labels", self => RptNav.OpenBarcodeGenerator(self)),
        // Security & activity
        Ribbon("ToolsWin.CardPermissions", "ToolsWin.CardPermissionsDesc", "permissions users roles templates", "OtherTools.Permissions"),
        Ribbon("ToolsWin.CardActivityLog", "ToolsWin.CardActivityLogDesc", "activity log security audit trail", "Tools.ActivityLog"),
        Ribbon("ToolsWin.CardChangePassword", "ToolsWin.CardChangePasswordDesc", "change password user", "Tools.ChangePassword"),
        Ribbon("ToolsWin.CardLicense", "ToolsWin.CardLicenseDesc", "licence license renewal activation", "Help.RenewLicense"),
        Ribbon("ToolsWin.CardAddons", "ToolsWin.CardAddonsDesc", "addons modules installed", "Tools.Addons"),
        Ribbon("ToolsWin.CardUpdates", "ToolsWin.CardUpdatesDesc", "updates check version", "Help.CheckUpdates"),
        // Definitions
        Ribbon("ToolsWin.CardUnits", "ToolsWin.CardUnitsDesc", "units measure definitions", "OtherTools.UnitsOfMeasure"),
        Ribbon("ToolsWin.CardPaymentMethods", "ToolsWin.CardPaymentMethodsDesc", "payment methods definitions accounts", "OtherTools.PaymentMethods"),
        Ribbon("ToolsWin.CardCustomFields", "ToolsWin.CardCustomFieldsDesc", "custom fields definitions", "OtherTools.CustomFields"),
        Ribbon("ToolsWin.CardMaterialSpecs", "ToolsWin.CardMaterialSpecsDesc", "material specs specifications definitions", "OtherTools.MaterialSpecs"),
        Ribbon("ToolsWin.CardDrivers", "ToolsWin.CardDriversDesc", "drivers delivery cards", "OtherTools.DriverCard"),
        Ribbon("ToolsWin.CardDeliveryPrices", "ToolsWin.CardDeliveryPricesDesc", "delivery prices definitions", "OtherTools.DeliveryPrices"),
        // Utilities
        Ribbon("ToolsWin.CardCalculator", "ToolsWin.CardCalculatorDesc", "calculator", "Tools.Calculator"),
        Ribbon("ToolsWin.CardKeyboard", "ToolsWin.CardKeyboardDesc", "keyboard on-screen touch", "Tools.Keyboard"),
        new("ToolsWin.CardOptions", "ToolsWin.CardOptionsDesc", "options settings control center", self => self.LaunchWindow(new OptWin())),
        Ribbon("ToolsWin.CardHelp", "ToolsWin.CardHelpDesc", "help manual f1 instructions", "Help.Instructions"),
        Ribbon("ToolsWin.CardSupport", "ToolsWin.CardSupportDesc", "support suggestions tickets", "Help.TechnicalSupport"),
    };

    // Catalog index ranges per section (Maintenance, Data, Design,
    // Security, Definitions, Utilities) — contiguous by construction.
    private static readonly (int From, int Count)[] SectionRanges =
        { (0, 4), (4, 5), (9, 4), (13, 6), (19, 6), (25, 5) };

    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(TextBlock Label, string Key)> _sectionLabels = new();
    private readonly Dictionary<CheckBox, string> _checkKeys = new();
    private readonly List<(TextBlock Title, TextBlock Desc, ToolEntry Entry)> _cardTexts = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _saveButton = new();
    private readonly Button _closeButton = new();

    private readonly ListBox _rail = new();
    private readonly Grid _bodyHost = new();
    private readonly List<UIElement> _sectionPanels = new();
    private readonly List<string> _sectionKeys = new();
    private int _backupSection = -1;
    private bool _healthRunning;

    // Overview
    private readonly TextBox _txtSearch = new();
    private readonly ListBox _searchResults = new();
    private readonly Button _btnRunChecks = new();
    private readonly CheckBox _chkAutoHealth = new();
    private readonly TextBlock _valCompany = new();
    private readonly TextBlock _valProvider = new();
    private readonly TextBlock _valDatabase = new();
    private readonly TextBlock _valSchema = new();
    private readonly TextBlock _valConnection = new();
    private readonly TextBlock _valIntegrity = new();
    private readonly TextBlock _valTrialBalance = new();
    private readonly TextBlock _valBackups = new();
    private readonly TextBlock _valOutbox = new();
    private readonly TextBlock _valBin = new();

    // Backup & automation
    private readonly CheckBox _chkBackupEnabled = new();
    private readonly CheckBox _chkAutoBackup = new();
    private readonly CheckBox _chkBackupOnStartup = new();
    private readonly CheckBox _chkVerifyAfterBackup = new();
    private readonly TextBox _txtBackupInterval = new();
    private readonly TextBox _txtMaxBackups = new();
    private readonly TextBox _txtCycleInterval = new();
    private readonly TextBlock _lblAdminNote = new();
    private readonly TextBlock _lblLastBackup = new();
    private readonly TextBlock _lblBackupWarn = new();
    private readonly TextBlock _lblBackupDue = new();
    private readonly Button _btnBackupNow = new();
    private readonly Button _btnOpenBackup = new();
    private bool _canEditMachineSettings;

    // Utilities language row
    private readonly TextBlock _lblLanguage = new();
    private readonly Button _btnLangEn = new() { Content = "English" };
    private readonly Button _btnLangAr = new() { Content = "Arabic" };
    private readonly Button _btnLangFr = new() { Content = "Français" };
    private readonly Button _btnLangTr = new() { Content = "Türkçe" };

    public ToolsWin()
    {
        Width = 980;
        Height = 640;
        MinWidth = 620;
        MinHeight = 360;
        BuildUi();
        LoadSettings();
        Loaded += OnLoaded;
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_saveButton, "Common.Save");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _saveButton.Click += (_, _) => SaveSettings();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_saveButton, _closeButton));

        BuildOverviewSection();
        BuildBackupSection();
        BuildCardSection("ToolsWin.SectionMaintenance", 0);
        BuildCardSection("ToolsWin.SectionData", 1);
        BuildCardSection("ToolsWin.SectionDesign", 2);
        BuildCardSection("ToolsWin.SectionSecurity", 3);
        BuildCardSection("ToolsWin.SectionDefinitions", 4);
        BuildUtilitiesSection();

        var middle = new Grid();
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _rail.SelectionChanged += (_, _) => ShowSection(_rail.SelectedIndex);
        Grid.SetColumn(_rail, 0);
        middle.Children.Add(_rail);
        Grid.SetColumn(_bodyHost, 1);
        middle.Children.Add(_bodyHost);
        root.Children.Add(middle);
        ShowSection(0);

        Content = root;
        ApplyLocalizedText();
    }

    // ------------------------------------------------------------ sections

    private Grid BeginSection(string key)
    {
        var header = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 10, 0, 6)
        };
        _sectionLabels.Add((header, key));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var panel = new StackPanel { Margin = new Thickness(12, 0, 4, 0) };
        panel.Children.Add(header);
        panel.Children.Add(grid);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
            Visibility = Visibility.Collapsed
        };
        _sectionPanels.Add(scroll);
        _bodyHost.Children.Add(scroll);
        _sectionKeys.Add(key);
        _rail.Items.Add(new ListBoxItem());
        return grid;
    }

    private void ShowSection(int index)
    {
        if (index < 0 || index >= _sectionPanels.Count)
            index = 0;
        for (var i = 0; i < _sectionPanels.Count; i++)
            _sectionPanels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        if (_rail.SelectedIndex != index)
            _rail.SelectedIndex = index;
        if (index == _backupSection)
            RefreshBackupInfo();
    }

    private void AddField(Grid grid, string key, Control control)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 8, 3),
            TextWrapping = TextWrapping.Wrap
        };
        _labels.Add((label, key));
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        control.Margin = new Thickness(0, 3, 0, 3);
        FlowAlign.Start(control);
        control.MinWidth = 220;
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private void AddCheck(Grid grid, CheckBox check, string key)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        check.Margin = new Thickness(0, 4, 0, 4);
        _checkKeys[check] = key;
        Grid.SetRow(check, row);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);
    }

    private void AddReadout(Grid grid, string key, TextBlock value)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 8, 3),
            TextWrapping = TextWrapping.Wrap
        };
        _labels.Add((label, key));
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        value.Margin = new Thickness(0, 3, 0, 3);
        value.TextWrapping = TextWrapping.Wrap;
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
    }

    private void AddCard(Grid grid, ToolEntry entry)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock { FontWeight = FontWeights.SemiBold };
        var desc = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(desc);
        var card = new Button
        {
            Content = stack,
            Margin = new Thickness(0, 3, 0, 3),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        FlowAlign.Start(card);
        card.Click += (_, _) => entry.Launch(this);
        _cardTexts.Add((title, desc, entry));
        Grid.SetRow(card, row);
        Grid.SetColumn(card, 1);
        grid.Children.Add(card);
    }

    private void BuildCardSection(string key, int rangeIndex)
    {
        var grid = BeginSection(key);
        var (from, count) = SectionRanges[rangeIndex];
        for (var i = from; i < from + count; i++)
            AddCard(grid, Catalog[i]);
    }

    private void BuildOverviewSection()
    {
        var grid = BeginSection("ToolsWin.SectionOverview");

        AddField(grid, "ToolsWin.SearchLabel", _txtSearch);
        _txtSearch.TextChanged += (_, _) => RefreshSearch();

        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _searchResults.Height = 132;
        _searchResults.Margin = new Thickness(0, 3, 0, 3);
        _searchResults.MouseDoubleClick += (_, _) => OpenSearchSelection();
        _searchResults.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                OpenSearchSelection();
        };
        Grid.SetRow(_searchResults, row);
        Grid.SetColumn(_searchResults, 1);
        grid.Children.Add(_searchResults);

        AddCheck(grid, _chkAutoHealth, "ToolsWin.AutoHealth");

        F2Ui.StyleButton(_btnRunChecks, "ToolsWin.RunChecks");
        _btnRunChecks.Click += async (_, _) => await RunHealthChecksAsync();
        var btnRow = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        FlowAlign.Start(_btnRunChecks);
        Grid.SetRow(_btnRunChecks, btnRow);
        Grid.SetColumn(_btnRunChecks, 1);
        grid.Children.Add(_btnRunChecks);

        AddReadout(grid, "ToolsWin.HealthCompany", _valCompany);
        AddReadout(grid, "ToolsWin.HealthProvider", _valProvider);
        AddReadout(grid, "ToolsWin.HealthDatabase", _valDatabase);
        AddReadout(grid, "ToolsWin.HealthSchema", _valSchema);
        AddReadout(grid, "ToolsWin.HealthConnection", _valConnection);
        AddReadout(grid, "ToolsWin.HealthIntegrity", _valIntegrity);
        AddReadout(grid, "ToolsWin.HealthTrialBalance", _valTrialBalance);
        AddReadout(grid, "ToolsWin.HealthBackups", _valBackups);
        AddReadout(grid, "ToolsWin.HealthOutbox", _valOutbox);
        AddReadout(grid, "ToolsWin.HealthBin", _valBin);
    }

    private void BuildBackupSection()
    {
        var grid = BeginSection("ToolsWin.SectionBackup");
        _backupSection = _sectionPanels.Count - 1;

        AddReadout(grid, "ToolsWin.HealthBackups", _lblLastBackup);
        _lblBackupWarn.FontWeight = FontWeights.SemiBold;
        AddReadout(grid, "ToolsWin.BackupDueLabel", _lblBackupDue);
        var warnRow = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _lblBackupWarn.Margin = new Thickness(0, 2, 0, 2);
        _lblBackupWarn.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_lblBackupWarn, warnRow);
        Grid.SetColumn(_lblBackupWarn, 1);
        grid.Children.Add(_lblBackupWarn);

        AddCheck(grid, _chkBackupEnabled, "ToolsWin.BackupEnabled");
        AddCheck(grid, _chkAutoBackup, "ToolsWin.AutoBackup");
        AddCheck(grid, _chkBackupOnStartup, "ToolsWin.BackupOnStartup");
        AddField(grid, "ToolsWin.BackupInterval", _txtBackupInterval);
        AddField(grid, "ToolsWin.MaxBackups", _txtMaxBackups);
        AddField(grid, "ToolsWin.CycleInterval", _txtCycleInterval);
        AddCheck(grid, _chkVerifyAfterBackup, "ToolsWin.VerifyAfterBackup");

        var noteRow = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _lblAdminNote.Margin = new Thickness(0, 4, 0, 4);
        _lblAdminNote.TextWrapping = TextWrapping.Wrap;
        _lblAdminNote.Opacity = 0.75;
        Grid.SetRow(_lblAdminNote, noteRow);
        Grid.SetColumn(_lblAdminNote, 1);
        grid.Children.Add(_lblAdminNote);

        F2Ui.StyleButton(_btnBackupNow, "ToolsWin.BackupNow");
        F2Ui.StyleButton(_btnOpenBackup, "ToolsWin.OpenBackupWindow");
        _btnBackupNow.Click += async (_, _) => await BackupNowAsync();
        _btnOpenBackup.Click += (_, _) =>
        {
            new BkpWin { Owner = this }.ShowDialog();
            RefreshBackupInfo();
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_btnBackupNow);
        actions.Children.Add(_btnOpenBackup);
        var actRow = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(actions, actRow);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
    }

    private void BuildUtilitiesSection()
    {
        var grid = BeginSection("ToolsWin.SectionUtilities");
        var (from, count) = SectionRanges[5];
        for (var i = from; i < from + count; i++)
            AddCard(grid, Catalog[i]);

        // Language row: the same live switch the ribbon language
        // buttons use (App.SetApplicationLanguage).
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _lblLanguage.VerticalAlignment = VerticalAlignment.Center;
        _lblLanguage.Margin = new Thickness(0, 3, 8, 3);
        _labels.Add((_lblLanguage, "ToolsWin.LanguageLabel"));
        Grid.SetRow(_lblLanguage, row);
        Grid.SetColumn(_lblLanguage, 0);
        grid.Children.Add(_lblLanguage);

        foreach (var b in new[] { _btnLangEn, _btnLangAr, _btnLangFr, _btnLangTr })
            F2Ui.StyleButton(b, string.Empty);
        _btnLangEn.Click += (_, _) => App.SetApplicationLanguage("en");
        _btnLangAr.Click += (_, _) => App.SetApplicationLanguage("ar");
        _btnLangFr.Click += (_, _) => App.SetApplicationLanguage("fr");
        _btnLangTr.Click += (_, _) => App.SetApplicationLanguage("tr");
        var langs = new StackPanel { Orientation = Orientation.Horizontal };
        langs.Children.Add(_btnLangEn);
        langs.Children.Add(_btnLangAr);
        langs.Children.Add(_btnLangFr);
        langs.Children.Add(_btnLangTr);
        Grid.SetRow(langs, row);
        Grid.SetColumn(langs, 1);
        grid.Children.Add(langs);
    }

    // ------------------------------------------------------------ launching

    private void LaunchRibbon(string ribbonKey)
    {
        try
        {
            if (!RibbonNav.TryHandle(ribbonKey, this))
                _statusBar.Text = T("ToolsWin.LaunchFailed");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "ToolsWin.LaunchRibbon");
            _statusBar.Text = T("ToolsWin.LaunchFailed");
        }
    }

    private void LaunchWindow(Window window)
    {
        try
        {
            window.Owner = this;
            window.Show();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "ToolsWin.LaunchWindow");
            _statusBar.Text = T("ToolsWin.LaunchFailed");
        }
    }

    private void RefreshSearch()
    {
        var query = _txtSearch.Text.Trim();
        _searchResults.Items.Clear();
        if (query.Length < 2)
            return;
        foreach (var entry in Catalog)
        {
            var name = T(entry.NameKey);
            var desc = T(entry.DescKey);
            if (name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                desc.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                entry.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase))
                _searchResults.Items.Add(new ListBoxItem { Content = name, Tag = entry });
        }
    }

    private void OpenSearchSelection()
    {
        if (_searchResults.SelectedItem is ListBoxItem { Tag: ToolEntry entry })
            entry.Launch(this);
    }

    // ------------------------------------------------------------ settings

    private void LoadSettings()
    {
        _chkAutoHealth.IsChecked = PrefGet("AutoHealth") != "0";
        _chkVerifyAfterBackup.IsChecked = PrefGet("VerifyAfterBackup") != "0";

        _canEditMachineSettings = AppHost.Permissions.Has(PermCat.System.Admin);
        var cfg = AppCfgSvc.Current;
        if (cfg is null)
        {
            _canEditMachineSettings = false;
        }
        else
        {
            _chkBackupEnabled.IsChecked = cfg.Database.Backup.Enabled;
            _chkAutoBackup.IsChecked = cfg.Database.Backup.AutoBackup;
            _chkBackupOnStartup.IsChecked = cfg.OnlineServices.BackupOnStartup;
            _txtBackupInterval.Text = cfg.Database.Backup.BackupIntervalHours.ToString(CultureInfo.InvariantCulture);
            _txtMaxBackups.Text = cfg.Database.Backup.MaxBackups.ToString(CultureInfo.InvariantCulture);
            _txtCycleInterval.Text = cfg.OnlineServices.BackgroundSyncIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        }
        foreach (var control in new Control[]
                 {
                     _chkBackupEnabled, _chkAutoBackup, _chkBackupOnStartup,
                     _txtBackupInterval, _txtMaxBackups, _txtCycleInterval
                 })
            control.IsEnabled = _canEditMachineSettings;
        _lblAdminNote.Visibility = _canEditMachineSettings ? Visibility.Collapsed : Visibility.Visible;

        foreach (var value in new[]
                 {
                     _valCompany, _valProvider, _valDatabase, _valSchema, _valConnection,
                     _valIntegrity, _valTrialBalance, _valBackups, _valOutbox, _valBin
                 })
            value.Text = T("ToolsWin.HealthNotRun");
    }

    private void SaveSettings()
    {
        // Per-user hub preferences always save (WinPref, this window's
        // own scope); the machine settings below are SYS.ADMIN-gated.
        PrefSet("AutoHealth", _chkAutoHealth.IsChecked == true ? "1" : "0");
        PrefSet("VerifyAfterBackup", _chkVerifyAfterBackup.IsChecked == true ? "1" : "0");

        if (!_canEditMachineSettings || AppCfgSvc.Current is not { } cfg)
        {
            _statusBar.Text = T("ToolsWin.Saved");
            return;
        }
        if (!TryInt(_txtBackupInterval, "ToolsWin.BackupInterval", out var interval) ||
            !TryInt(_txtMaxBackups, "ToolsWin.MaxBackups", out var maxBackups) ||
            !TryInt(_txtCycleInterval, "ToolsWin.CycleInterval", out var cycle))
            return;

        cfg.Database.Backup.Enabled = _chkBackupEnabled.IsChecked == true;
        cfg.Database.Backup.AutoBackup = _chkAutoBackup.IsChecked == true;
        cfg.OnlineServices.BackupOnStartup = _chkBackupOnStartup.IsChecked == true;
        cfg.Database.Backup.BackupIntervalHours = Math.Clamp(interval, 1, 8760);
        cfg.Database.Backup.MaxBackups = Math.Clamp(maxBackups, 1, 1000);
        cfg.OnlineServices.BackgroundSyncIntervalSeconds = Math.Clamp(cycle, 15, 86400);

        if (string.IsNullOrWhiteSpace(AppCfgSvc.LoadedPath))
        {
            // AppCfgSvc.Save() would silently no-op: the app is running
            // on built-in defaults. Report it through the catalog and
            // tell the user plainly instead of claiming a save.
            AppHost.Errors.Report(ErrorCodes.ToolsSettingsNotPersisted, null, "ToolsWin.SaveSettings");
            _statusBar.Text = AppHost.Errors.Resolve(ErrorCodes.ToolsSettingsNotPersisted).Message;
            return;
        }
        AppCfgSvc.Save();
        _statusBar.Text = T("ToolsWin.Saved");
        RefreshBackupInfo();
    }

    private bool TryInt(TextBox box, string labelKey, out int value)
    {
        if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
            int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
            return true;
        MessageBox.Show(this, Tf("ToolsWin.InvalidNumber", T(labelKey)), Title,
            MessageBoxButton.OK, MessageBoxImage.Warning);
        box.Focus();
        return false;
    }

    // ------------------------------------------------------------ backup

    private void RefreshBackupInfo()
    {
        try
        {
            var company = AppState.CurrentCompanyCode ?? string.Empty;
            var files = string.IsNullOrWhiteSpace(company)
                ? new List<BkpFileInfo>()
                : AppHost.Backup.ListBackups(company);
            if (files.Count == 0)
            {
                _lblLastBackup.Text = T("ToolsWin.BackupNever");
                _lblBackupWarn.Visibility = Visibility.Collapsed;
                _lblBackupDue.Text = T("ToolsWin.BackupDue");
                return;
            }
            var newest = files[0].LastWriteUtc;
            foreach (var f in files)
                if (f.LastWriteUtc > newest)
                    newest = f.LastWriteUtc;
            var days = (int)Math.Floor((DateTime.UtcNow - newest).TotalDays);
            _lblLastBackup.Text = Tf("ToolsWin.BackupValue",
                files.Count.ToString(CultureInfo.InvariantCulture),
                newest.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                days.ToString(CultureInfo.InvariantCulture));
            _lblBackupWarn.Text = T("ToolsWin.BackupStale");
            _lblBackupWarn.Visibility = days > 7 ? Visibility.Visible : Visibility.Collapsed;

            // Due state for THIS company, same rule the engine applies
            // (study §3 records that BkpServ.IsBackupDue itself still
            // inspects the legacy "001" folder only).
            var cfg = AppCfgSvc.Current;
            var due = cfg is not null && cfg.Database.Backup.Enabled && cfg.Database.Backup.AutoBackup &&
                      (DateTime.UtcNow - newest).TotalHours >= cfg.Database.Backup.BackupIntervalHours;
            _lblBackupDue.Text = T(due ? "ToolsWin.BackupDue" : "ToolsWin.BackupNotDue");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "ToolsWin.RefreshBackupInfo");
            _lblLastBackup.Text = T("ToolsWin.BackupNever");
            _lblBackupWarn.Visibility = Visibility.Collapsed;
        }
    }

    private async Task BackupNowAsync()
    {
        var company = AppState.CurrentCompanyCode ?? string.Empty;
        if (string.IsNullOrWhiteSpace(company))
            return;
        try
        {
            _btnBackupNow.IsEnabled = false;
            var path = await AppHost.Backup.CreateBackupAsync(company);
            var message = Tf("ToolsWin.BackupCreated", Path.GetFileName(path));
            if (_chkVerifyAfterBackup.IsChecked == true)
            {
                var ok = await AppHost.Backup.ValidateBackupAsync(path);
                message += " " + T(ok ? "ToolsWin.BackupVerified" : "ToolsWin.BackupVerifyFailed");
            }
            _statusBar.Text = message;
            RefreshBackupInfo();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.ToolsBackupActionFailed, ex, "ToolsWin.BackupNow");
            MessageBox.Show(this, AppHost.Errors.Resolve(ErrorCodes.ToolsBackupActionFailed).Message,
                Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _btnBackupNow.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------ health

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_chkAutoHealth.IsChecked == true)
            await RunHealthChecksAsync();
    }

    private async Task RunHealthChecksAsync()
    {
        if (_healthRunning)
            return;
        _healthRunning = true;
        _btnRunChecks.IsEnabled = false;
        try
        {
            var company = AppState.CurrentCompanyCode ?? string.Empty;
            _valCompany.Text = string.IsNullOrWhiteSpace(company) ? "—" : company;
            _valProvider.Text = CurrentDatabaseProvider;
            _valDatabase.Text = DescribeDatabase();

            var ops = AppHost.Operations;
            if (ops is not null && !string.IsNullOrWhiteSpace(company))
            {
                try
                {
                    var schema = await ops.GetSchemaVersionAsync();
                    _valSchema.Text = schema.AppliedCount == 0
                        ? T("ToolsWin.SchemaNone")
                        : Tf("ToolsWin.SchemaValue",
                            schema.AppliedCount.ToString(CultureInfo.InvariantCulture),
                            schema.HighestMigrationId);
                }
                catch (Exception ex)
                {
                    AppHost.Errors.Report(ErrorCodes.ToolsHealthCheckFailed, ex, "ToolsWin.Health.Schema");
                }

                try
                {
                    _valIntegrity.Text = await ops.GetIntegritySummaryAsync();
                }
                catch (Exception ex)
                {
                    AppHost.Errors.Report(ErrorCodes.ToolsHealthCheckFailed, ex, "ToolsWin.Health.Integrity");
                }

                try
                {
                    var tb = await ops.GetTrialBalanceIntegrityAsync(company);
                    var issues = tb.UnbalancedEntries.Count + tb.OrphanLineCount + tb.PostedEntriesWithoutLines;
                    _valTrialBalance.Text = tb.IsClean
                        ? T("ToolsWin.TbClean")
                        : Tf("ToolsWin.TbIssues", issues.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                {
                    AppHost.Errors.Report(ErrorCodes.ToolsHealthCheckFailed, ex, "ToolsWin.Health.TrialBalance");
                }

                try
                {
                    var messages = await ops.GetOutboundMessagesAsync(company);
                    var queued = messages.Count(m => string.Equals(m.Status, "QUEUED", StringComparison.OrdinalIgnoreCase));
                    var failed = messages.Count(m => string.Equals(m.Status, "FAILED", StringComparison.OrdinalIgnoreCase));
                    var sent = messages.Count(m => string.Equals(m.Status, "SENT", StringComparison.OrdinalIgnoreCase));
                    _valOutbox.Text = Tf("ToolsWin.OutboxValue",
                        queued.ToString(CultureInfo.InvariantCulture),
                        failed.ToString(CultureInfo.InvariantCulture),
                        sent.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                {
                    AppHost.Errors.Report(ErrorCodes.ToolsHealthCheckFailed, ex, "ToolsWin.Health.Outbox");
                }
            }

            try
            {
                var health = AppHost.DatabaseHealth;
                if (health is not null)
                {
                    var result = await health.CheckAsync();
                    _valConnection.Text = result.IsAvailable
                        ? T("ToolsWin.ConnOk") + " — " + result.Message
                        : T("ToolsWin.ConnFailed") + " — " + result.Message;
                }
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.ToolsHealthCheckFailed, ex, "ToolsWin.Health.Connection");
            }

            try
            {
                var files = string.IsNullOrWhiteSpace(company)
                    ? new List<BkpFileInfo>()
                    : AppHost.Backup.ListBackups(company);
                if (files.Count == 0)
                {
                    _valBackups.Text = T("ToolsWin.BackupNever");
                }
                else
                {
                    var newest = files[0].LastWriteUtc;
                    foreach (var f in files)
                        if (f.LastWriteUtc > newest)
                            newest = f.LastWriteUtc;
                    var days = (int)Math.Floor((DateTime.UtcNow - newest).TotalDays);
                    _valBackups.Text = Tf("ToolsWin.BackupValue",
                        files.Count.ToString(CultureInfo.InvariantCulture),
                        newest.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                        days.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.ToolsHealthCheckFailed, ex, "ToolsWin.Health.Backups");
            }

            try
            {
                var count = string.IsNullOrWhiteSpace(company)
                    ? 0
                    : await RecycleBinSvc.CountAsync(company);
                _valBin.Text = Tf("ToolsWin.BinValue", count.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.ToolsHealthCheckFailed, ex, "ToolsWin.Health.Bin");
            }
        }
        finally
        {
            _healthRunning = false;
            _btnRunChecks.IsEnabled = true;
        }
    }

    private static string DescribeDatabase()
    {
        try
        {
            if (DatabaseRuntime.IsPostgreSql())
                return "PostgreSQL";
            var connection = DatabaseRuntime.GetConnectionString() ?? string.Empty;
            foreach (var part in connection.Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 &&
                    kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                {
                    var path = kv[1].Trim();
                    if (File.Exists(path))
                    {
                        var mb = new FileInfo(path).Length / 1048576.0;
                        return $"{Path.GetFileName(path)} ({mb.ToString("0.0", CultureInfo.InvariantCulture)} MB)";
                    }
                    return Path.GetFileName(path);
                }
            }
            return "SQLite";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.ToolsHealthCheckFailed, ex, "ToolsWin.DescribeDatabase");
            return "—";
        }
    }

    // ------------------------------------------------------------ state & text

    protected override void OnRestoreWindowState()
    {
        if (int.TryParse(PrefGet("Section"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var idx))
            ShowSection(idx);
    }

    protected override void OnSaveWindowState()
    {
        PrefSet("Section", _rail.SelectedIndex.ToString(CultureInfo.InvariantCulture));
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("ToolsWin.Title");
        F2Ui.ApplyTexts(_labels, new List<(DataGridColumn, string)>(),
            new[] { _saveButton, _closeButton, _btnRunChecks, _btnBackupNow, _btnOpenBackup }, T);
        for (var i = 0; i < _sectionKeys.Count && i < _rail.Items.Count; i++)
            if (_rail.Items[i] is ListBoxItem item)
                item.Content = T(_sectionKeys[i]);
        foreach (var (label, key) in _sectionLabels)
            label.Text = T(key);
        foreach (var (check, key) in _checkKeys)
            check.Content = T(key);
        foreach (var (title, desc, entry) in _cardTexts)
        {
            title.Text = T(entry.NameKey);
            desc.Text = T(entry.DescKey);
        }
        _lblAdminNote.Text = T("ToolsWin.AdminNote");
        RefreshSearch();
        if (_sectionPanels.Count > 0 && _rail.SelectedIndex == _backupSection)
            RefreshBackupInfo();
    }
}
