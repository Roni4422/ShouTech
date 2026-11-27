using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Views;

/// <summary>
/// Options window (services phase B1): the Al-Ameen-style single
/// options surface over the per-user preferences (UsrPref) that were
/// previously scattered across the shell menus and startup dialogs.
/// Every field edits the stored preference and persists through
/// UsrPref.Save(); language and accent additionally apply live
/// through the same entry points the shell uses
/// (AppHost.Localization.SetLanguage / AppHost.Visual.SetAccent).
/// The remaining settings take effect through the app's existing
/// read points (startup, window creation, behavior checks).
///
/// The Tools.Options ribbon key routes here; the POS cash-drawer
/// options keep their own window, opened from this window's POS
/// section. The product's visual identity is Light-only (AppHost),
/// so no theme choice is offered — nothing is faked.
/// </summary>
public sealed class OptWin : BaseWin
{
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(TextBlock Label, string Key, string ScopeKey)> _sectionLabels = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _saveButton = new();
    private readonly Button _posButton = new();
    private readonly Button _closeButton = new();
    private readonly Button _resetPositionsButton = new();
    private readonly Button _resetLayoutsButton = new();
    private readonly Button _resetAllButton = new();

    private readonly ComboBox _cmbLang = new();
    private readonly TextBox _txtCurrency = new();
    private readonly ComboBox _cmbAccent = new();
    private readonly TextBox _txtFontSize = new();
    private readonly ComboBox _cmbDateFormat = new();
    private readonly TextBox _txtAutoSave = new();
    private readonly TextBox _txtAutoLock = new();

    // Font section (OPTWIN_SECTIONS §3.1): family picker + size +
    // live preview rendered in the chosen typography.
    private readonly ComboBox _cmbFontFamily = new();
    private readonly ComboBoxItem _fontDefaultItem = new();
    private readonly TextBlock _fontPreviewHead = new();
    private readonly TextBlock _fontPreviewBody = new();
    private readonly TextBlock _fontPreviewSmall = new();

    // Numbers, date & time section (§3.2): the digit split and
    // display rules behind NumFmt, with a live sample rendered by
    // the real formatter core from the edited (unsaved) values.
    private readonly TextBox _txtQtyDecimals = new();
    private readonly TextBox _txtPriceDecimals = new();
    private readonly CheckBox _chkGroupThousands = new();
    private readonly CheckBox _chkShowZeros = new();
    private readonly CheckBox _chkRoundDisplay = new();
    private readonly ComboBox _cmbTimeFormat = new();
    private readonly TextBlock _lblNumSample = new();

    private readonly CheckBox _chkCompact = new();
    private readonly CheckBox _chkToolBar = new();
    private readonly CheckBox _chkStatusBar = new();
    private readonly CheckBox _chkAnimations = new();
    private readonly CheckBox _chkConfirmExit = new();
    private readonly CheckBox _chkConfirmDelete = new();
    private readonly CheckBox _chkAutoComplete = new();
    private readonly CheckBox _chkAutoFocus = new();
    private readonly CheckBox _chkToolTips = new();
    private readonly CheckBox _chkAutoOpenCompany = new();
    private readonly CheckBox _chkSkipSplash = new();

    // Category rail (SVC1): one section body visible at a time,
    // Phenix Options model (rail + body + bottom buttons).
    private readonly ListBox _rail = new();
    private readonly Grid _bodyHost = new();
    private readonly List<UIElement> _sectionPanels = new();
    private readonly List<string> _sectionKeys = new();
    private readonly TextBlock _lblLastBackup = new();
    private readonly TextBlock _lblBackupWarn = new();
    private readonly Button _btnBackup = new();
    private int _backupSection = -1;

    // Numbering section (NUMBERING_STUDY §3.4): per-series prefix /
    // padding / yearly reset / start, effective for future numbers.
    private readonly DataGrid _numGrid = new();
    private readonly TextBlock _numHint = new();
    private readonly ObservableCollection<NumRowVm> _numRows = new();
    private int _numberingSection = -1;

    // Printing section (§3.3): company print settings (print_settings,
    // migration 125) + the company logo + per-user copy counts. The
    // logo is staged in memory and written on Save, like every other
    // field in this window.
    private readonly CheckBox _chkHdrName = new();
    private readonly CheckBox _chkHdrAddress = new();
    private readonly CheckBox _chkHdrPhone = new();
    private readonly CheckBox _chkHdrMobile = new();
    private readonly CheckBox _chkHdrEmail = new();
    private readonly CheckBox _chkHdrWebsite = new();
    private readonly CheckBox _chkHdrLicense = new();
    private readonly CheckBox _chkHdrTax = new();
    private readonly TextBox _txtHeaderText = new();
    private readonly CheckBox _chkPrevBalance = new();
    private readonly CheckBox _chkCurrBalance = new();
    private readonly CheckBox _chkUserOnInvoice = new();
    private readonly ComboBox _cmbPrinter = new();
    private readonly ComboBoxItem _printerDefaultItem = new();
    private readonly TextBox _txtInvoiceCopies = new();
    private readonly TextBox _txtVoucherCopies = new();
    private readonly Image _imgLogo = new();
    private readonly Button _btnLogoPick = new();
    private readonly Button _btnLogoClear = new();
    private readonly Button _btnTestPage = new();
    private int _printingSection = -1;
    private bool _printingLoaded;
    private byte[]? _logoStaged;
    private string? _logoStagedMedia;
    private bool _logoDirty;

    // Barcode section (§3.4): the composite scale-label template
    // (barcode_templates, migration 126). Three named segment rows
    // (code / weight integer / weight fraction) + a live test field
    // that runs the real parser — our layout, not a copy of the
    // benchmark's free segment grid.
    private readonly CheckBox _chkBcdActive = new();
    private readonly TextBox _txtBcdPrefix = new();
    private readonly TextBox _txtBcdTotal = new();
    private readonly TextBox _txtBcdCodeStart = new();
    private readonly TextBox _txtBcdCodeLen = new();
    private readonly TextBox _txtBcdIntStart = new();
    private readonly TextBox _txtBcdIntLen = new();
    private readonly TextBox _txtBcdFracStart = new();
    private readonly TextBox _txtBcdFracLen = new();
    private readonly TextBox _txtBcdTest = new();
    private readonly TextBlock _lblBcdTestResult = new();
    private int _barcodeSection = -1;
    private bool _barcodeLoaded;

    private UsrPref _pref = UsrPref.Load();

    public OptWin()
    {
        Width = 760;
        Height = 620;
        MinWidth = 620;
        MinHeight = 360;
        BuildUi();
        LoadFields();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_saveButton, "Common.Save");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _saveButton.Click += (_, _) => Save();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_saveButton, _closeButton));

        // ---- General ------------------------------------------------
        var general = BeginSection("Options.General", "Options.ScopePersonal");
        AddField(general, "Options.Language", _cmbLang);
        // Language names follow the LangVM convention (Latin script),
        // keeping source files English-only per the catalog rule.
        _cmbLang.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        _cmbLang.Items.Add(new ComboBoxItem { Content = "Arabic", Tag = "ar" });
        _cmbLang.Items.Add(new ComboBoxItem { Content = "Français", Tag = "fr" });
        _cmbLang.Items.Add(new ComboBoxItem { Content = "Türkçe", Tag = "tr" });
        AddField(general, "Options.Currency", _txtCurrency);

        // ---- Appearance ---------------------------------------------
        var appearance = BeginSection("Options.Appearance", "Options.ScopePersonal");
        AddField(appearance, "Options.Accent", _cmbAccent);
        foreach (var accent in VisuId.AvailableAccents)
            _cmbAccent.Items.Add(new ComboBoxItem { Content = accent.Display, Tag = accent.Key });
        AddCheck(appearance, _chkCompact, "Options.CompactMode");
        AddCheck(appearance, _chkToolBar, "Options.ShowToolBar");
        AddCheck(appearance, _chkStatusBar, "Options.ShowStatusBar");
        AddCheck(appearance, _chkAnimations, "Options.EnableAnimations");

        // ---- Font (§3.1) ---------------------------------------------
        // The family/size pair is consumed by the ONE central point
        // (VisuId.ApplyShellVisualProperties) for every window; the
        // preview card renders the sample lines in the edited
        // typography before anything is saved.
        var font = BeginSection("Options.FontSection", "Options.ScopePersonal");
        _fontDefaultItem.Tag = string.Empty;
        _cmbFontFamily.Items.Add(_fontDefaultItem);
        foreach (var family in Fonts.SystemFontFamilies
                     .Select(f => f.Source)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            _cmbFontFamily.Items.Add(new ComboBoxItem { Content = family, Tag = family });
        AddField(font, "Options.FontFamily", _cmbFontFamily);
        AddField(font, "Options.FontSize", _txtFontSize);
        var fontPreview = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 6, 0, 2)
        };
        fontPreview.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        fontPreview.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        var fontPreviewStack = new StackPanel();
        _fontPreviewHead.FontWeight = FontWeights.SemiBold;
        _fontPreviewBody.Margin = new Thickness(0, 4, 0, 0);
        _fontPreviewSmall.Margin = new Thickness(0, 2, 0, 0);
        _fontPreviewSmall.Opacity = 0.75;
        fontPreviewStack.Children.Add(_fontPreviewHead);
        fontPreviewStack.Children.Add(_fontPreviewBody);
        fontPreviewStack.Children.Add(_fontPreviewSmall);
        fontPreview.Child = fontPreviewStack;
        var fontPreviewRow = font.RowDefinitions.Count;
        font.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(fontPreview, fontPreviewRow);
        Grid.SetColumn(fontPreview, 1);
        font.Children.Add(fontPreview);
        _cmbFontFamily.SelectionChanged += (_, _) => RefreshFontPreview();
        _txtFontSize.TextChanged += (_, _) => RefreshFontPreview();

        // ---- Numbers, date & time (§3.2) ------------------------------
        var numbers = BeginSection("Options.NumbersSection", "Options.ScopePersonal");
        AddField(numbers, "Options.QtyDecimals", _txtQtyDecimals);
        AddField(numbers, "Options.PriceDecimals", _txtPriceDecimals);
        AddCheck(numbers, _chkGroupThousands, "Options.GroupThousands");
        AddCheck(numbers, _chkShowZeros, "Options.ShowTrailingZeros");
        AddCheck(numbers, _chkRoundDisplay, "Options.RoundDisplay");
        AddField(numbers, "Options.DateFormat", _cmbDateFormat);
        foreach (var fmt in new[] { "dd/MM/yyyy", "yyyy-MM-dd", "MM/dd/yyyy", "dd.MM.yyyy" })
            _cmbDateFormat.Items.Add(new ComboBoxItem { Content = fmt, Tag = fmt });
        AddField(numbers, "Options.TimeFormat", _cmbTimeFormat);
        foreach (var fmt in new[] { "HH:mm", "h:mm tt", "hh:mm tt", "H:mm" })
            _cmbTimeFormat.Items.Add(new ComboBoxItem { Content = fmt, Tag = fmt });
        AddReadout(numbers, _lblNumSample);
        _txtQtyDecimals.TextChanged += (_, _) => RefreshNumSample();
        _txtPriceDecimals.TextChanged += (_, _) => RefreshNumSample();
        _chkGroupThousands.Checked += (_, _) => RefreshNumSample();
        _chkGroupThousands.Unchecked += (_, _) => RefreshNumSample();
        _chkShowZeros.Checked += (_, _) => RefreshNumSample();
        _chkShowZeros.Unchecked += (_, _) => RefreshNumSample();
        _chkRoundDisplay.Checked += (_, _) => RefreshNumSample();
        _chkRoundDisplay.Unchecked += (_, _) => RefreshNumSample();
        _cmbDateFormat.SelectionChanged += (_, _) => RefreshNumSample();
        _cmbTimeFormat.SelectionChanged += (_, _) => RefreshNumSample();

        // ---- Behavior -----------------------------------------------
        var behavior = BeginSection("Options.Behavior", "Options.ScopePersonal");
        AddCheck(behavior, _chkConfirmExit, "Options.ConfirmOnExit");
        AddCheck(behavior, _chkConfirmDelete, "Options.ConfirmOnDelete");
        AddCheck(behavior, _chkAutoComplete, "Options.EnableAutoComplete");
        AddCheck(behavior, _chkAutoFocus, "Options.AutoFocusFirstField");
        AddCheck(behavior, _chkToolTips, "Options.ShowToolTips");
        AddField(behavior, "Options.AutoSaveInterval", _txtAutoSave);

        // ---- Session & startup --------------------------------------
        var session = BeginSection("Options.Session", "Options.ScopePersonal");
        AddCheck(session, _chkAutoOpenCompany, "Options.AutoOpenLastCompany");
        AddCheck(session, _chkSkipSplash, "Options.SkipSplash");
        AddField(session, "Options.AutoLock", _txtAutoLock);

        // ---- Point of sale ------------------------------------------
        var pos = BeginSection("Options.PosSection", "Options.ScopeCompany");
        F2Ui.StyleButton(_posButton, "Options.PosDevices");
        FlowAlign.Start(_posButton);
        _posButton.Click += (_, _) =>
        {
            var drawer = new CashDrawerOptionsWin { Owner = this };
            drawer.ShowDialog();
        };
        pos.Children.Add(_posButton);

        // ---- Window layouts (per-user window state) ------------------
        // Reset clears USER-scoped state only (positions / grid
        // layouts / everything); company defaults and other users
        // are untouched. Open windows re-save their state on close.
        var layouts = BeginSection("Options.LayoutsSection", "Options.ScopePersonal");
        F2Ui.StyleButton(_resetPositionsButton, "Options.ResetPositions");
        F2Ui.StyleButton(_resetLayoutsButton, "Options.ResetLayouts");
        F2Ui.StyleButton(_resetAllButton, "Options.ResetAllLayouts");
        _resetPositionsButton.Click += (_, _) => ResetLayouts(WinPref.ResetKind.Geometry);
        _resetLayoutsButton.Click += (_, _) => ResetLayouts(WinPref.ResetKind.Layouts);
        _resetAllButton.Click += (_, _) => ResetLayouts(WinPref.ResetKind.All);
        var layoutButtons = new StackPanel { Orientation = Orientation.Horizontal };
        layoutButtons.Children.Add(_resetPositionsButton);
        layoutButtons.Children.Add(_resetLayoutsButton);
        layoutButtons.Children.Add(_resetAllButton);
        Grid.SetColumn(layoutButtons, 1);
        layouts.Children.Add(layoutButtons);

        // ---- Printing (§3.3) ------------------------------------------
        // Grouped cards in spirit: paper identity (logo + header
        // fields + header text), then invoice behaviour (balances,
        // user line), then the workstation's printer + copy counts.
        var printing = BeginSection("Options.PrintingSection", "Options.ScopeCompany");
        _printingSection = _sectionPanels.Count - 1;
        var logoRow = printing.RowDefinitions.Count;
        printing.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var logoPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        _imgLogo.Height = 54;
        _imgLogo.MaxWidth = 180;
        _imgLogo.Margin = new Thickness(0, 0, 10, 0);
        logoPanel.Children.Add(_imgLogo);
        F2Ui.StyleButton(_btnLogoPick, "Options.PrintLogoPick");
        F2Ui.StyleButton(_btnLogoClear, "Options.PrintLogoClear");
        _btnLogoPick.Click += (_, _) => PickLogo();
        _btnLogoClear.Click += (_, _) =>
        {
            _logoStaged = null;
            _logoStagedMedia = null;
            _logoDirty = true;
            _imgLogo.Source = null;
        };
        logoPanel.Children.Add(_btnLogoPick);
        logoPanel.Children.Add(_btnLogoClear);
        Grid.SetRow(logoPanel, logoRow);
        Grid.SetColumn(logoPanel, 1);
        printing.Children.Add(logoPanel);
        AddCheck(printing, _chkHdrName, "Options.PrintHeaderName");
        AddCheck(printing, _chkHdrAddress, "Options.PrintHeaderAddress");
        AddCheck(printing, _chkHdrPhone, "Options.PrintHeaderPhone");
        AddCheck(printing, _chkHdrMobile, "Options.PrintHeaderMobile");
        AddCheck(printing, _chkHdrEmail, "Options.PrintHeaderEmail");
        AddCheck(printing, _chkHdrWebsite, "Options.PrintHeaderWebsite");
        AddCheck(printing, _chkHdrLicense, "Options.PrintHeaderLicense");
        AddCheck(printing, _chkHdrTax, "Options.PrintHeaderTaxNumber");
        AddField(printing, "Options.PrintHeaderText", _txtHeaderText);
        AddCheck(printing, _chkPrevBalance, "Options.PrintPreviousBalance");
        AddCheck(printing, _chkCurrBalance, "Options.PrintCurrentBalance");
        AddCheck(printing, _chkUserOnInvoice, "Options.PrintUserOnInvoice");
        _printerDefaultItem.Tag = string.Empty;
        _cmbPrinter.Items.Add(_printerDefaultItem);
        foreach (var printer in CashDrawerService.GetInstalledPrinters())
            _cmbPrinter.Items.Add(new ComboBoxItem { Content = printer, Tag = printer });
        AddField(printing, "Options.PrintDefaultPrinter", _cmbPrinter);
        AddField(printing, "Options.PrintInvoiceCopies", _txtInvoiceCopies);
        AddField(printing, "Options.PrintVoucherCopies", _txtVoucherCopies);
        F2Ui.StyleButton(_btnTestPage, "Options.PrintTestPage");
        FlowAlign.Start(_btnTestPage);
        _btnTestPage.Click += (_, _) => PrintTestPage();
        var testRow = printing.RowDefinitions.Count;
        printing.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_btnTestPage, testRow);
        Grid.SetColumn(_btnTestPage, 1);
        printing.Children.Add(_btnTestPage);

        // ---- Barcode (§3.4) ------------------------------------------
        var barcode = BeginSection("Options.BarcodeSection", "Options.ScopeCompany");
        _barcodeSection = _sectionPanels.Count - 1;
        AddCheck(barcode, _chkBcdActive, "Options.BcdActive");
        AddField(barcode, "Options.BcdPrefix", _txtBcdPrefix);
        AddField(barcode, "Options.BcdTotalLength", _txtBcdTotal);
        AddSegmentRow(barcode, "Options.BcdCodeSegment", _txtBcdCodeStart, _txtBcdCodeLen);
        AddSegmentRow(barcode, "Options.BcdWeightIntSegment", _txtBcdIntStart, _txtBcdIntLen);
        AddSegmentRow(barcode, "Options.BcdWeightFracSegment", _txtBcdFracStart, _txtBcdFracLen);
        AddField(barcode, "Options.BcdTestLabel", _txtBcdTest);
        AddReadout(barcode, _lblBcdTestResult);
        foreach (var box in new[]
                 {
                     _txtBcdPrefix, _txtBcdTotal, _txtBcdCodeStart, _txtBcdCodeLen,
                     _txtBcdIntStart, _txtBcdIntLen, _txtBcdFracStart, _txtBcdFracLen, _txtBcdTest
                 })
            box.TextChanged += (_, _) => UpdateBcdTest();

        // ---- Backup (SVC1) -------------------------------------------
        // Readout is DERIVED from the real backup list (newest
        // file's write time) so it cannot drift from reality; the
        // staleness warning adopts the Phenix dashboard pattern
        // (warn when the last backup is older than 7 days).
        var backup = BeginSection("Options.BackupSection", "Options.ScopeCompany");
        _backupSection = _sectionPanels.Count - 1;
        AddReadout(backup, _lblLastBackup);
        AddReadout(backup, _lblBackupWarn);
        _lblBackupWarn.FontWeight = FontWeights.SemiBold;
        F2Ui.StyleButton(_btnBackup, "Options.OpenBackup");
        FlowAlign.Start(_btnBackup);
        _btnBackup.Click += (_, _) =>
        {
            new BkpWin { Owner = this }.ShowDialog();
            RefreshBackupInfo();
        };
        var backupRow = backup.RowDefinitions.Count;
        backup.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_btnBackup, backupRow);
        Grid.SetColumn(_btnBackup, 1);
        backup.Children.Add(_btnBackup);

        // ---- Numbering (per-series configuration) -------------------
        var numbering = BeginSection("Options.NumberingSection", "Options.ScopeCompany");
        _numberingSection = _sectionPanels.Count - 1;
        AddReadout(numbering, _numHint);
        BuildNumberingGrid();
        var numRow = numbering.RowDefinitions.Count;
        numbering.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_numGrid, numRow);
        Grid.SetColumn(_numGrid, 1);
        numbering.Children.Add(_numGrid);

        // Rail + body host (rail on the start side; mirrors in RTL).
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

    private static void AddReadout(Grid grid, TextBlock block)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        block.Margin = new Thickness(0, 4, 0, 4);
        block.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(block, row);
        Grid.SetColumn(block, 1);
        grid.Children.Add(block);
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
        if (index == _numberingSection)
            LoadNumbering();
        if (index == _printingSection)
            LoadPrinting();
        if (index == _barcodeSection)
            LoadBarcode();
    }

    private void LoadPrinting()
    {
        if (_printingLoaded)
            return;
        _printingLoaded = true;
        try
        {
            var settings = PrintSetSvc.Load();
            _chkHdrName.IsChecked = settings.HeaderName;
            _chkHdrAddress.IsChecked = settings.HeaderAddress;
            _chkHdrPhone.IsChecked = settings.HeaderPhone;
            _chkHdrMobile.IsChecked = settings.HeaderMobile;
            _chkHdrEmail.IsChecked = settings.HeaderEmail;
            _chkHdrWebsite.IsChecked = settings.HeaderWebsite;
            _chkHdrLicense.IsChecked = settings.HeaderLicense;
            _chkHdrTax.IsChecked = settings.HeaderTaxNumber;
            _txtHeaderText.Text = settings.HeaderText ?? string.Empty;
            _chkPrevBalance.IsChecked = settings.PrintPreviousBalance;
            _chkCurrBalance.IsChecked = settings.PrintCurrentBalance;
            _chkUserOnInvoice.IsChecked = settings.PrintUserOnInvoice;
            SelectByTag(_cmbPrinter, settings.DefaultPrinter ?? string.Empty);
            _logoStaged = PrintSetSvc.LoadLogo()?.Data;
            _logoDirty = false;
            ShowLogoPreview();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OptWin.LoadPrinting");
        }
        _txtInvoiceCopies.Text = _pref.PrintInvoiceCopies.ToString(CultureInfo.InvariantCulture);
        _txtVoucherCopies.Text = _pref.PrintVoucherCopies.ToString(CultureInfo.InvariantCulture);
    }

    private void ShowLogoPreview()
    {
        if (_logoStaged is not { Length: > 0 } bytes)
        {
            _imgLogo.Source = null;
            return;
        }
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            using (var stream = new System.IO.MemoryStream(bytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            _imgLogo.Source = bitmap;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "OptWin.ShowLogoPreview");
            _imgLogo.Source = null;
        }
    }

    private void PickLogo()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true)
            return;
        try
        {
            var bytes = System.IO.File.ReadAllBytes(dlg.FileName);
            if (bytes.Length > 2 * 1024 * 1024)
            {
                MessageBox.Show(this, T("Options.PrintLogoTooLarge"), Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
            _logoStagedMedia = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => null
            };
            if (_logoStagedMedia is null)
            {
                MessageBox.Show(this, T("Options.PrintLogoInvalid"), Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _logoStaged = bytes;
            _logoDirty = true;
            ShowLogoPreview();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OptWin.PickLogo");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool SavePrinting()
    {
        if (!_printingLoaded)
            return true;
        try
        {
            var actor = AppState.CurrentUser?.Username ?? "anonymous";
            PrintSetSvc.Save(new PrintSetSvc.PrintSettingsRec(
                _chkHdrName.IsChecked == true,
                _chkHdrAddress.IsChecked == true,
                _chkHdrPhone.IsChecked == true,
                _chkHdrMobile.IsChecked == true,
                _chkHdrEmail.IsChecked == true,
                _chkHdrWebsite.IsChecked == true,
                _chkHdrLicense.IsChecked == true,
                _chkHdrTax.IsChecked == true,
                _txtHeaderText.Text,
                _chkPrevBalance.IsChecked == true,
                _chkCurrBalance.IsChecked == true,
                _chkUserOnInvoice.IsChecked == true,
                SelectedTag(_cmbPrinter)), actor);
            if (_logoDirty)
            {
                if (_logoStaged is { Length: > 0 })
                    PrintSetSvc.SaveLogo(_logoStaged, _logoStagedMedia);
                else
                    PrintSetSvc.ClearLogo();
                _logoDirty = false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OptWin.SavePrinting");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>One barcode segment row: the segment's label in the
    /// label column, then Start/Length number boxes side by side —
    /// positions are read off the printed label left to right.</summary>
    private void AddSegmentRow(Grid grid, string key, TextBox startBox, TextBox lengthBox)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 8, 3)
        };
        _labels.Add((label, key));
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var startLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        _labels.Add((startLabel, "Options.BcdStart"));
        var lengthLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        _labels.Add((lengthLabel, "Options.BcdLength"));
        startBox.Width = 56;
        lengthBox.Width = 56;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        panel.Children.Add(startLabel);
        panel.Children.Add(startBox);
        panel.Children.Add(lengthLabel);
        panel.Children.Add(lengthBox);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
    }

    private void LoadBarcode()
    {
        if (_barcodeLoaded)
            return;
        _barcodeLoaded = true;
        try
        {
            var row = BcdTplSvc.LoadRow();
            _chkBcdActive.IsChecked = row?.Active ?? false;
            if (row is { } stored)
            {
                _txtBcdPrefix.Text = stored.Template.Prefix;
                _txtBcdTotal.Text = stored.Template.TotalLength.ToString(CultureInfo.InvariantCulture);
                _txtBcdCodeStart.Text = stored.Template.CodeStart.ToString(CultureInfo.InvariantCulture);
                _txtBcdCodeLen.Text = stored.Template.CodeLength.ToString(CultureInfo.InvariantCulture);
                _txtBcdIntStart.Text = stored.Template.WeightIntStart.ToString(CultureInfo.InvariantCulture);
                _txtBcdIntLen.Text = stored.Template.WeightIntLength.ToString(CultureInfo.InvariantCulture);
                _txtBcdFracStart.Text = stored.Template.WeightFracStart.ToString(CultureInfo.InvariantCulture);
                _txtBcdFracLen.Text = stored.Template.WeightFracLength.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                // Editor defaults = the standard scale-label layout;
                // nothing parses until a prefix is set and the
                // template is activated and saved.
                _txtBcdTotal.Text = "13";
                _txtBcdCodeStart.Text = "3";
                _txtBcdCodeLen.Text = "4";
                _txtBcdIntStart.Text = "7";
                _txtBcdIntLen.Text = "2";
                _txtBcdFracStart.Text = "9";
                _txtBcdFracLen.Text = "3";
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OptWin.LoadBarcode");
        }
        UpdateBcdTest();
    }

    private bool TryBuildBarcodeTemplate(out BarcodeTemplate template)
    {
        template = null!;
        if (!int.TryParse(_txtBcdTotal.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) ||
            !int.TryParse(_txtBcdCodeStart.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cs) ||
            !int.TryParse(_txtBcdCodeLen.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cl) ||
            !int.TryParse(_txtBcdIntStart.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ws) ||
            !int.TryParse(_txtBcdIntLen.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wl) ||
            !int.TryParse(_txtBcdFracStart.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fs) ||
            !int.TryParse(_txtBcdFracLen.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fl))
            return false;
        template = new BarcodeTemplate(_txtBcdPrefix.Text.Trim(), total, cs, cl, ws, wl, fs, fl);
        return template.IsValid(out _);
    }

    private bool SaveBarcode()
    {
        if (!_barcodeLoaded)
            return true;
        try
        {
            var actor = AppState.CurrentUser?.Username ?? "anonymous";
            if (_txtBcdPrefix.Text.Trim().Length == 0)
                return BcdTplSvc.Save(null, false, actor);
            if (!TryBuildBarcodeTemplate(out var template))
            {
                MessageBox.Show(this, T("Options.BcdInvalid"), Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowSection(_barcodeSection);
                return false;
            }
            return BcdTplSvc.Save(template, _chkBcdActive.IsChecked == true, actor);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OptWin.SaveBarcode");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>The live test runs the REAL parser over the unsaved
    /// field values, so what the owner sees here is exactly what the
    /// POS scan box and the invoice grid will do with the label.</summary>
    private void UpdateBcdTest()
    {
        var sample = _txtBcdTest.Text.Trim();
        if (sample.Length == 0 || !TryBuildBarcodeTemplate(out var template))
        {
            _lblBcdTestResult.Text = string.Empty;
            return;
        }
        _lblBcdTestResult.Text = template.TryParse(sample, out var code, out var weight)
            ? Tf("Options.BcdTestResult", code, weight.ToString("0.###", CultureInfo.InvariantCulture))
            : T("Options.BcdTestNoMatch");
    }

    /// <summary>Prints a sample document through the REAL invoice
    /// pipeline, so the governed header/logo/balances are seen
    /// exactly as customers will see them.</summary>
    private void PrintTestPage()
    {
        try
        {
            var doc = new InvDoc
            {
                Kind = InvDocKind.SalesInvoice,
                DocNo = "TEST-0001",
                DocDate = DateTime.Today,
                CurrencyCode = _pref.CurrencyCode,
                PartyCode = "TEST",
                PartyName = T("Options.PrintTestParty"),
                PostedBy = AppState.CurrentUser?.Username
            };
            doc.Lines.Add(new InvLine
            {
                LineNo = 1,
                MaterialCode = "TEST",
                Description = T("Options.PrintTestItem"),
                Unit = "pcs",
                Qty = 2m,
                UnitPrice = 25.5m
            });
            InvPrn.Print(doc, CurrentCompanyDisplay);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OptWin.PrintTestPage");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>One editable row of the Numbering grid.</summary>
    private sealed class NumRowVm
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Prefix { get; set; } = string.Empty;
        public long StartValue { get; set; }
        public long CurrentValue { get; set; }
    }

    private void BuildNumberingGrid()
    {
        _numGrid.AutoGenerateColumns = false;
        _numGrid.CanUserAddRows = false;
        _numGrid.CanUserDeleteRows = false;
        _numGrid.Height = 320;
        _numGrid.ItemsSource = _numRows;
        _numGrid.Columns.Add(new DataGridTextColumn { Binding = new System.Windows.Data.Binding(nameof(NumRowVm.Name)) { Mode = System.Windows.Data.BindingMode.OneWay }, Width = 170, IsReadOnly = true });
        _numGrid.Columns.Add(new DataGridTextColumn { Binding = new System.Windows.Data.Binding(nameof(NumRowVm.Prefix)), Width = 70 });
        _numGrid.Columns.Add(new DataGridTextColumn { Binding = new System.Windows.Data.Binding(nameof(NumRowVm.StartValue)), Width = 80 });
        _numGrid.Columns.Add(new DataGridTextColumn { Binding = new System.Windows.Data.Binding(nameof(NumRowVm.CurrentValue)) { Mode = System.Windows.Data.BindingMode.OneWay }, Width = 80, IsReadOnly = true });
    }

    private void LoadNumbering()
    {
        _numHint.Text = T("Numbering.Hint");
        var headers = new[] { "Numbering.Series", "Numbering.Prefix", "Numbering.StartValue", "Numbering.CurrentValue" };
        for (var i = 0; i < headers.Length && i < _numGrid.Columns.Count; i++)
            _numGrid.Columns[i].Header = T(headers[i]);
        _numRows.Clear();
        try
        {
            foreach (var row in SeqSvc.LoadSeries())
                _numRows.Add(new NumRowVm
                {
                    Key = row.Key,
                    Name = T(row.NameKey),
                    Prefix = row.Prefix,
                    StartValue = row.StartValue,
                    CurrentValue = row.CurrentValue
                });
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OptWin.LoadNumbering");
        }
    }

    private bool SaveNumbering()
    {
        if (_numRows.Count == 0)
            return true;
        try
        {
            var actor = AppState.CurrentUser?.Username ?? "anonymous";
            SeqSvc.SaveSeries(
                _numRows.Select(r => new SeqSvc.SeriesRow(r.Key, string.Empty, r.Prefix, 1, false, r.StartValue, r.CurrentValue)),
                actor);
            return true;
        }
        catch (AppError ex)
        {
            AppHost.Errors.Report(ex.Code, ex, "OptWin.SaveNumbering");
            var info = AppHost.Errors.Resolve(ex.Code, ex.Args.ToArray());
            MessageBox.Show(this, info.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OptWin.SaveNumbering");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

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
                _lblLastBackup.Text = T("Options.BackupNever");
                _lblBackupWarn.Visibility = Visibility.Collapsed;
                return;
            }
            var newest = files[0].LastWriteUtc;
            foreach (var f in files)
                if (f.LastWriteUtc > newest)
                    newest = f.LastWriteUtc;
            var days = (int)Math.Floor((DateTime.UtcNow - newest).TotalDays);
            _lblLastBackup.Text = Tf("Options.BackupLastDays",
                newest.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
                days.ToString(CultureInfo.InvariantCulture));
            _lblBackupWarn.Text = T("Options.BackupStale");
            _lblBackupWarn.Visibility = days > 7 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OptWin.RefreshBackupInfo");
            _lblLastBackup.Text = T("Options.BackupNever");
            _lblBackupWarn.Visibility = Visibility.Collapsed;
        }
    }

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

    private Grid BeginSection(string key, string scopeKey)
    {
        var header = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 10, 0, 6)
        };
        _sectionLabels.Add((header, key, scopeKey));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
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

    private void AddField(Grid grid, string key, Control control)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 8, 3)
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
        // The check's own content carries the label; ApplyLocalizedText
        // sets it from the key.
        _checkKeys[check] = key;
        Grid.SetRow(check, row);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);
    }

    private readonly Dictionary<CheckBox, string> _checkKeys = new();

    private void LoadFields()
    {
        _pref = UsrPref.Load();
        SelectByTag(_cmbLang, string.IsNullOrWhiteSpace(_pref.Lang) ? "en" : _pref.Lang);
        _txtCurrency.Text = _pref.CurrencyCode;
        SelectByTag(_cmbAccent,
            string.IsNullOrWhiteSpace(_pref.AccentColor) ? "slate" : _pref.AccentColor.ToLowerInvariant());
        SelectByTag(_cmbFontFamily, _pref.FontFamily ?? string.Empty);
        _txtFontSize.Text = _pref.FontSize.ToString("0", CultureInfo.InvariantCulture);
        _txtQtyDecimals.Text = _pref.QtyDecimalPlaces.ToString(CultureInfo.InvariantCulture);
        _txtPriceDecimals.Text = _pref.PriceDecimalPlaces.ToString(CultureInfo.InvariantCulture);
        _chkGroupThousands.IsChecked = _pref.GroupThousands;
        _chkShowZeros.IsChecked = _pref.ShowTrailingZeros;
        _chkRoundDisplay.IsChecked = _pref.RoundDisplay;
        SelectByTag(_cmbDateFormat, _pref.DateFormat);
        SelectByTag(_cmbTimeFormat, _pref.TimeFormat);
        _txtAutoSave.Text = _pref.AutoSaveIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        _txtAutoLock.Text = _pref.AutoLockMinutes.ToString(CultureInfo.InvariantCulture);

        _chkCompact.IsChecked = _pref.CompactMode;
        _chkToolBar.IsChecked = _pref.ShowToolBar;
        _chkStatusBar.IsChecked = _pref.ShowStatusBar;
        _chkAnimations.IsChecked = _pref.EnableAnimations;
        _chkConfirmExit.IsChecked = _pref.ConfirmOnExit;
        _chkConfirmDelete.IsChecked = _pref.ConfirmOnDelete;
        _chkAutoComplete.IsChecked = _pref.EnableAutoComplete;
        _chkAutoFocus.IsChecked = _pref.AutoFocusFirstField;
        _chkToolTips.IsChecked = _pref.ShowToolTips;
        _chkAutoOpenCompany.IsChecked = _pref.AutoOpenLastCompany;
        _chkSkipSplash.IsChecked = _pref.SkipSplash;
        RefreshFontPreview();
        RefreshNumSample();
    }

    /// <summary>Renders the Font preview card in the edited (unsaved)
    /// family/size — the same values VisuId will apply to every
    /// window on save.</summary>
    private void RefreshFontPreview()
    {
        var familyName = SelectedTag(_cmbFontFamily);
        FontFamily family;
        if (!string.IsNullOrWhiteSpace(familyName))
        {
            family = new FontFamily(familyName);
        }
        else
        {
            family = TryFindResource("ApplicationFontFamily") as FontFamily
                ?? new FontFamily("Segoe UI");
        }
        var size = double.TryParse(_txtFontSize.Text, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 10, 20)
            : _pref.FontSize;
        foreach (var block in new[] { _fontPreviewHead, _fontPreviewBody, _fontPreviewSmall })
            block.FontFamily = family;
        _fontPreviewHead.FontSize = size + 5;
        _fontPreviewBody.FontSize = size;
        _fontPreviewSmall.FontSize = Math.Max(size - 2, 8);
    }

    /// <summary>Renders the Numbers live sample through the REAL
    /// formatter core with the edited (unsaved) settings, so what
    /// the user sees is exactly what the program will print.</summary>
    private void RefreshNumSample()
    {
        var qtyDigits = TryParseDigits(_txtQtyDecimals.Text, _pref.QtyDecimalPlaces);
        var priceDigits = TryParseDigits(_txtPriceDecimals.Text, _pref.PriceDecimalPlaces);
        var settings = new NumFmtSettings(
            qtyDigits,
            priceDigits,
            _chkGroupThousands.IsChecked == true,
            _chkShowZeros.IsChecked == true,
            _chkRoundDisplay.IsChecked == true,
            SelectedTag(_cmbDateFormat),
            SelectedTag(_cmbTimeFormat));
        var sampleMoment = new DateTime(2026, 2, 3, 14, 5, 0);
        _lblNumSample.Text = Tf("Options.NumSample",
            NumFmt.FormatValue(12345.6789m, qtyDigits, settings),
            NumFmt.FormatValue(9876.543m, priceDigits, settings),
            NumFmt.FormatDate(sampleMoment, settings.DatePattern),
            NumFmt.FormatTimeOfDay(sampleMoment, settings.TimePattern));
    }

    private static int TryParseDigits(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0, 4)
            : fallback;

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem ci &&
                string.Equals(ci.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = ci;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private void Save()
    {
        if (!TryInt(_txtFontSize, "Options.FontSize", out var fontSize) ||
            !TryInt(_txtQtyDecimals, "Options.QtyDecimals", out var qtyDecimals) ||
            !TryInt(_txtPriceDecimals, "Options.PriceDecimals", out var priceDecimals) ||
            !TryInt(_txtAutoSave, "Options.AutoSaveInterval", out var autoSave) ||
            !TryInt(_txtAutoLock, "Options.AutoLock", out var autoLock))
            return;
        var invoiceCopies = _pref.PrintInvoiceCopies;
        var voucherCopies = _pref.PrintVoucherCopies;
        if (_printingLoaded &&
            (!TryInt(_txtInvoiceCopies, "Options.PrintInvoiceCopies", out invoiceCopies) ||
             !TryInt(_txtVoucherCopies, "Options.PrintVoucherCopies", out voucherCopies)))
            return;

        // Numbering configuration validates and persists first: an
        // invalid series row aborts the whole save (NUM-8302).
        _numGrid.CommitEdit();
        if (!SaveNumbering())
            return;
        // Print settings + logo persist with the same all-or-nothing
        // discipline.
        if (!SavePrinting())
            return;
        if (!SaveBarcode())
            return;
        _pref.PrintInvoiceCopies = Math.Clamp(invoiceCopies, 1, 9);
        _pref.PrintVoucherCopies = Math.Clamp(voucherCopies, 1, 9);

        var oldLang = _pref.Lang;
        var oldAccent = _pref.AccentColor;

        _pref.Lang = SelectedTag(_cmbLang);
        _pref.LangSet = true;
        _pref.CurrencyCode = string.IsNullOrWhiteSpace(_txtCurrency.Text)
            ? _pref.CurrencyCode : _txtCurrency.Text.Trim().ToUpperInvariant();
        _pref.AccentColor = SelectedTag(_cmbAccent);
        _pref.FontFamily = SelectedTag(_cmbFontFamily);
        _pref.FontSize = Math.Clamp(fontSize, 10, 20);
        _pref.QtyDecimalPlaces = Math.Clamp(qtyDecimals, 0, 4);
        _pref.PriceDecimalPlaces = Math.Clamp(priceDecimals, 0, 4);
        _pref.GroupThousands = _chkGroupThousands.IsChecked == true;
        _pref.ShowTrailingZeros = _chkShowZeros.IsChecked == true;
        _pref.RoundDisplay = _chkRoundDisplay.IsChecked == true;
        _pref.DateFormat = SelectedTag(_cmbDateFormat);
        _pref.TimeFormat = SelectedTag(_cmbTimeFormat);
        _pref.AutoSaveIntervalMinutes = Math.Max(0, autoSave);
        _pref.AutoLockMinutes = Math.Max(0, autoLock);

        _pref.CompactMode = _chkCompact.IsChecked == true;
        _pref.ShowToolBar = _chkToolBar.IsChecked == true;
        _pref.ShowStatusBar = _chkStatusBar.IsChecked == true;
        _pref.EnableAnimations = _chkAnimations.IsChecked == true;
        _pref.ConfirmOnExit = _chkConfirmExit.IsChecked == true;
        _pref.ConfirmOnDelete = _chkConfirmDelete.IsChecked == true;
        _pref.EnableAutoComplete = _chkAutoComplete.IsChecked == true;
        _pref.AutoFocusFirstField = _chkAutoFocus.IsChecked == true;
        _pref.ShowToolTips = _chkToolTips.IsChecked == true;
        _pref.AutoOpenLastCompany = _chkAutoOpenCompany.IsChecked == true;
        _pref.SkipSplash = _chkSkipSplash.IsChecked == true;

        try
        {
            _pref.Save();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OptWin.Save");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Live-apply the settings with established shell appliers.
        if (!string.Equals(oldAccent, _pref.AccentColor, StringComparison.OrdinalIgnoreCase))
            AppHost.Visual.SetAccent(_pref.AccentColor);
        if (!string.Equals(oldLang, _pref.Lang, StringComparison.OrdinalIgnoreCase))
            AppHost.Localization.SetLanguage(_pref.Lang);

        // Number formats take effect program-wide at once: every
        // consumer reads NumFmt.Current (§3.2).
        NumFmt.Current = _pref.ToNumFmtSettings();

        // Font applies to open windows immediately, and to every
        // future window through VisuId (§3.1).
        try
        {
            if (System.Windows.Application.Current is not null)
            {
                foreach (Window window in System.Windows.Application.Current.Windows)
                {
                    if (!string.IsNullOrWhiteSpace(_pref.FontFamily))
                        window.FontFamily = new FontFamily(_pref.FontFamily);
                    window.FontSize = _pref.FontSize;
                }
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "OptWin.Save.ApplyFont");
        }

        _statusBar.Text = T("Options.Saved");
    }

    private void ResetLayouts(WinPref.ResetKind kind)
    {
        if (MessageBox.Show(this, T("Options.ResetConfirm"), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var company = AppState.CurrentCompanyCode ?? "no-company";
        var user = AppState.CurrentUser?.Username ?? "anonymous";
        WinPref.ResetUserState(company, user, kind);
        _statusBar.Text = T("Options.LayoutsReset");
    }

    private bool TryInt(TextBox box, string labelKey, out int value)
    {
        if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
            int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
            return true;
        MessageBox.Show(this, Tf("Options.InvalidNumber", T(labelKey)), Title,
            MessageBoxButton.OK, MessageBoxImage.Warning);
        box.Focus();
        return false;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Options.Title");
        F2Ui.ApplyTexts(_labels, new List<(DataGridColumn, string)>(),
            new[] { _saveButton, _posButton, _closeButton, _resetPositionsButton, _resetLayoutsButton, _resetAllButton, _btnBackup, _btnLogoPick, _btnLogoClear, _btnTestPage }, T);
        _printerDefaultItem.Content = T("Options.PrintSystemDefault");
        for (var i = 0; i < _sectionKeys.Count && i < _rail.Items.Count; i++)
            if (_rail.Items[i] is ListBoxItem item)
                item.Content = T(_sectionKeys[i]);
        foreach (var (label, key, scopeKey) in _sectionLabels)
            label.Text = $"{T(key)}  ·  {T(scopeKey)}";
        _fontDefaultItem.Content = T("Options.FontThemeDefault");
        _fontPreviewHead.Text = T("Options.FontPreviewText");
        _fontPreviewBody.Text = T("Options.FontPreviewText");
        _fontPreviewSmall.Text = T("Options.FontPreviewText");
        RefreshNumSample();
        foreach (var (check, key) in _checkKeys)
            check.Content = T(key);
    }
}
