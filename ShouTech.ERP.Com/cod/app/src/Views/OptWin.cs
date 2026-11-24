using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Configuration;
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
    private readonly List<(TextBlock Label, string Key)> _sectionLabels = new();
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
    private readonly TextBox _txtDecimals = new();
    private readonly TextBox _txtThousandSep = new();
    private readonly TextBox _txtAutoSave = new();
    private readonly TextBox _txtAutoLock = new();

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
        var general = BeginSection("Options.General");
        AddField(general, "Options.Language", _cmbLang);
        // Language names follow the LangVM convention (Latin script),
        // keeping source files English-only per the catalog rule.
        _cmbLang.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        _cmbLang.Items.Add(new ComboBoxItem { Content = "Arabic", Tag = "ar" });
        _cmbLang.Items.Add(new ComboBoxItem { Content = "Français", Tag = "fr" });
        _cmbLang.Items.Add(new ComboBoxItem { Content = "Türkçe", Tag = "tr" });
        AddField(general, "Options.Currency", _txtCurrency);
        AddField(general, "Options.DateFormat", _cmbDateFormat);
        foreach (var fmt in new[] { "dd/MM/yyyy", "yyyy-MM-dd", "MM/dd/yyyy", "dd.MM.yyyy" })
            _cmbDateFormat.Items.Add(new ComboBoxItem { Content = fmt, Tag = fmt });
        AddField(general, "Options.DecimalPlaces", _txtDecimals);
        AddField(general, "Options.ThousandSeparator", _txtThousandSep);

        // ---- Appearance ---------------------------------------------
        var appearance = BeginSection("Options.Appearance");
        AddField(appearance, "Options.Accent", _cmbAccent);
        foreach (var accent in VisuId.AvailableAccents)
            _cmbAccent.Items.Add(new ComboBoxItem { Content = accent.Display, Tag = accent.Key });
        AddField(appearance, "Options.FontSize", _txtFontSize);
        AddCheck(appearance, _chkCompact, "Options.CompactMode");
        AddCheck(appearance, _chkToolBar, "Options.ShowToolBar");
        AddCheck(appearance, _chkStatusBar, "Options.ShowStatusBar");
        AddCheck(appearance, _chkAnimations, "Options.EnableAnimations");

        // ---- Behavior -----------------------------------------------
        var behavior = BeginSection("Options.Behavior");
        AddCheck(behavior, _chkConfirmExit, "Options.ConfirmOnExit");
        AddCheck(behavior, _chkConfirmDelete, "Options.ConfirmOnDelete");
        AddCheck(behavior, _chkAutoComplete, "Options.EnableAutoComplete");
        AddCheck(behavior, _chkAutoFocus, "Options.AutoFocusFirstField");
        AddCheck(behavior, _chkToolTips, "Options.ShowToolTips");
        AddField(behavior, "Options.AutoSaveInterval", _txtAutoSave);

        // ---- Session & startup --------------------------------------
        var session = BeginSection("Options.Session");
        AddCheck(session, _chkAutoOpenCompany, "Options.AutoOpenLastCompany");
        AddCheck(session, _chkSkipSplash, "Options.SkipSplash");
        AddField(session, "Options.AutoLock", _txtAutoLock);

        // ---- Point of sale ------------------------------------------
        var pos = BeginSection("Options.PosSection");
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
        var layouts = BeginSection("Options.LayoutsSection");
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

        // ---- Backup (SVC1) -------------------------------------------
        // Readout is DERIVED from the real backup list (newest
        // file's write time) so it cannot drift from reality; the
        // staleness warning adopts the Phenix dashboard pattern
        // (warn when the last backup is older than 7 days).
        var backup = BeginSection("Options.BackupSection");
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
        var numbering = BeginSection("Options.NumberingSection");
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
    }

    /// <summary>One editable row of the Numbering grid.</summary>
    private sealed class NumRowVm
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Prefix { get; set; } = string.Empty;
        public int Padding { get; set; }
        public bool Yearly { get; set; }
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
        _numGrid.Columns.Add(new DataGridTextColumn { Binding = new System.Windows.Data.Binding(nameof(NumRowVm.Padding)), Width = 60 });
        _numGrid.Columns.Add(new DataGridCheckBoxColumn { Binding = new System.Windows.Data.Binding(nameof(NumRowVm.Yearly)), Width = 70 });
        _numGrid.Columns.Add(new DataGridTextColumn { Binding = new System.Windows.Data.Binding(nameof(NumRowVm.StartValue)), Width = 80 });
        _numGrid.Columns.Add(new DataGridTextColumn { Binding = new System.Windows.Data.Binding(nameof(NumRowVm.CurrentValue)) { Mode = System.Windows.Data.BindingMode.OneWay }, Width = 80, IsReadOnly = true });
    }

    private void LoadNumbering()
    {
        _numHint.Text = T("Numbering.Hint");
        var headers = new[] { "Numbering.Series", "Numbering.Prefix", "Numbering.Padding", "Numbering.Yearly", "Numbering.StartValue", "Numbering.CurrentValue" };
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
                    Padding = row.Padding,
                    Yearly = row.Yearly,
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
                _numRows.Select(r => new SeqSvc.SeriesRow(r.Key, string.Empty, r.Prefix, r.Padding, r.Yearly, r.StartValue, r.CurrentValue)),
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
        _txtFontSize.Text = _pref.FontSize.ToString("0", CultureInfo.InvariantCulture);
        SelectByTag(_cmbDateFormat, _pref.DateFormat);
        _txtDecimals.Text = _pref.DecimalPlaces.ToString(CultureInfo.InvariantCulture);
        _txtThousandSep.Text = _pref.ThousandSeparator;
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
    }

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
            !TryInt(_txtDecimals, "Options.DecimalPlaces", out var decimals) ||
            !TryInt(_txtAutoSave, "Options.AutoSaveInterval", out var autoSave) ||
            !TryInt(_txtAutoLock, "Options.AutoLock", out var autoLock))
            return;

        // Numbering configuration validates and persists first: an
        // invalid series row aborts the whole save (NUM-8302).
        _numGrid.CommitEdit();
        if (!SaveNumbering())
            return;

        var oldLang = _pref.Lang;
        var oldAccent = _pref.AccentColor;

        _pref.Lang = SelectedTag(_cmbLang);
        _pref.LangSet = true;
        _pref.CurrencyCode = string.IsNullOrWhiteSpace(_txtCurrency.Text)
            ? _pref.CurrencyCode : _txtCurrency.Text.Trim().ToUpperInvariant();
        _pref.AccentColor = SelectedTag(_cmbAccent);
        _pref.FontSize = Math.Clamp(fontSize, 10, 20);
        _pref.DateFormat = SelectedTag(_cmbDateFormat);
        _pref.DecimalPlaces = Math.Clamp(decimals, 0, 4);
        _pref.DecimalDigits = _pref.DecimalPlaces;
        _pref.ThousandSeparator = string.IsNullOrEmpty(_txtThousandSep.Text)
            ? "," : _txtThousandSep.Text[..1];
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

        // Live-apply the two settings with established shell appliers.
        if (!string.Equals(oldAccent, _pref.AccentColor, StringComparison.OrdinalIgnoreCase))
            AppHost.Visual.SetAccent(_pref.AccentColor);
        if (!string.Equals(oldLang, _pref.Lang, StringComparison.OrdinalIgnoreCase))
            AppHost.Localization.SetLanguage(_pref.Lang);

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
            new[] { _saveButton, _posButton, _closeButton, _resetPositionsButton, _resetLayoutsButton, _resetAllButton, _btnBackup }, T);
        for (var i = 0; i < _sectionKeys.Count && i < _rail.Items.Count; i++)
            if (_rail.Items[i] is ListBoxItem item)
                item.Content = T(_sectionKeys[i]);
        foreach (var (label, key) in _sectionLabels)
            label.Text = T(key);
        foreach (var (check, key) in _checkKeys)
            check.Content = T(key);
    }
}
