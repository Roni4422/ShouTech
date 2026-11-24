using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Views;

/// <summary>
/// Company tax administration: jurisdiction opt-in, effective-dated tax
/// rules, recoverability, calculation mode, and GL account mapping.
/// Posted documents retain their immutable tax snapshots.
/// </summary>
public sealed class TaxSetupWin : BaseWin
{
    protected override string? ScheduleTintName => "Wicker";
    private readonly ComboBox _country = new();
    private readonly Button _jurisdictionAction = new();
    private readonly DataGrid _rules = new() { AutoGenerateColumns = true, IsReadOnly = true, CanUserAddRows = false };
    private readonly TextBlock _status = new();
    private readonly TextBox _registrationNumber = new();
    private readonly TextBox _code = new();
    private readonly TextBox _name = new();
    private readonly TextBox _type = new();
    private readonly TextBox _treatment = new();
    private readonly TextBox _rate = new();
    private readonly TextBox _outputAccount = new();
    private readonly TextBox _inputAccount = new();
    private readonly TextBox _sourceUrl = new();
    private readonly TextBox _sourceReference = new();
    private readonly ComboBox _calculation = new();
    private readonly DatePicker _effectiveFrom = new() { SelectedDate = DateTime.Today };
    private readonly DatePicker _effectiveTo = new();
    private readonly CheckBox _recoverable = new() { IsChecked = true };
    private readonly CheckBox _withholding = new();
    private readonly TextBox _withholdingAccount = new();
    private readonly TextBox _withholdingThreshold = new();
    private readonly ComboBox _withholdingMode = new();
    private readonly CheckBox _compound = new();
    private readonly CheckBox _default = new();
    private readonly Button _new = new();
    private readonly Button _save = new();
    private readonly Button _refresh = new();
    private readonly Button _close = new();
    private bool _loadingCountry;

    public TaxSetupWin()
    {
        Title = T("TaxSetup.Title");
        Width = 1120;
        Height = 760;
        // Screen-fit cap (verify_screenfit.py): <= 684x384. The rules
        // grid and the editor band share the middle in star rows (0.8*/1.2*,
        // their full-size proportions); the editor band scrolls vertically
        // and the grid scrolls internally when squeezed, and the top band
        // and action bar stay docked, always visible.
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("TaxSetup.Title");
        if (Content is DependencyObject root)
            ApplyLocalizedNode(root);
        foreach (var column in _rules.Columns)
            if (!string.IsNullOrWhiteSpace(column.SortMemberPath))
                column.Header = T("TaxSetup.Column." + column.SortMemberPath);
        if (IsLoaded)
            RefreshData();
    }

    private static void ApplyLocalizedNode(DependencyObject node)
    {
        if (node is TextBlock text && text.Tag is string textKey)
            text.Text = T(textKey);
        else if (node is Button button && button.Tag is string buttonKey)
            button.Content = T(buttonKey);
        else if (node is CheckBox check && check.Tag is string checkKey)
            check.Content = T(checkKey);

        foreach (var child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject dependency)
                ApplyLocalizedNode(dependency);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Accounting.TaxView))
        {
            MessageBox.Show(this, T("TaxSetup.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        _country.ItemsSource = TaxCountryPackCatalog.All;
        _country.DisplayMemberPath = nameof(TaxCountryPack.CountryName);
        _country.SelectedValuePath = nameof(TaxCountryPack.CountryCode);
        RefreshData();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12), LastChildFill = true };

        var top = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 8) };
        var jurisdiction = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        jurisdiction.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        jurisdiction.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        jurisdiction.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var countryLabel = new TextBlock { Text = T("TaxSetup.Jurisdiction"), Tag = "TaxSetup.Jurisdiction", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(countryLabel, 0);
        _country.MinWidth = 250;
        Grid.SetColumn(_country, 1);
        _jurisdictionAction.Content = T("TaxSetup.Enable");
        _jurisdictionAction.Margin = new Thickness(8, 0, 0, 0);
        _jurisdictionAction.Click += (_, _) => ChangeJurisdiction();
        Grid.SetColumn(_jurisdictionAction, 2);
        jurisdiction.Children.Add(countryLabel);
        jurisdiction.Children.Add(_country);
        jurisdiction.Children.Add(_jurisdictionAction);
        top.Children.Add(jurisdiction);
        var registrationPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        registrationPanel.Children.Add(new TextBlock
        {
            Text = T("TaxSetup.RegistrationNumber"),
            Tag = "TaxSetup.RegistrationNumber",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _registrationNumber.MinWidth = 250;
        registrationPanel.Children.Add(_registrationNumber);
        top.Children.Add(registrationPanel);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 0, 0, 4);
        top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        _rules.SelectionChanged += (_, _) => LoadSelectedRule();
        _rules.AutoGeneratingColumn += (_, e) =>
            e.Column.Header = T("TaxSetup.Column." + e.PropertyName);

        var editor = new Grid { Margin = new Thickness(0, 10, 0, 4) };
        for (var i = 0; i < 4; i++)
            editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 5; i++)
            editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddField(editor, 0, 0, "TaxSetup.Code", _code);
        AddField(editor, 0, 2, "TaxSetup.Name", _name);
        AddField(editor, 1, 0, "TaxSetup.Type", _type);
        AddField(editor, 1, 2, "TaxSetup.Treatment", _treatment);
        AddField(editor, 2, 0, "TaxSetup.Rate", _rate);
        AddField(editor, 2, 2, "TaxSetup.Calculation", _calculation);
        AddField(editor, 3, 0, "TaxSetup.EffectiveFrom", _effectiveFrom);
        AddField(editor, 3, 2, "TaxSetup.EffectiveTo", _effectiveTo);
        AddField(editor, 4, 0, "TaxSetup.SourceUrl", _sourceUrl);
        AddField(editor, 4, 2, "TaxSetup.SourceReference", _sourceReference);

        _calculation.ItemsSource = new[] { "Exclusive", "Inclusive" };
        _calculation.SelectedIndex = 0;
        _outputAccount.Text = "2205";
        _inputAccount.Text = "1205";

        var accountPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        accountPanel.Children.Add(FieldPanel("TaxSetup.OutputAccount", _outputAccount));
        accountPanel.Children.Add(FieldPanel("TaxSetup.InputAccount", _inputAccount));
        accountPanel.Children.Add(CheckPanel("TaxSetup.Recoverable", _recoverable));
        accountPanel.Children.Add(CheckPanel("TaxSetup.Compound", _compound));
        accountPanel.Children.Add(CheckPanel("TaxSetup.Default", _default));

        // Withholding (W1): marking the rule as a withholding category
        // reveals its payable account + threshold configuration; the
        // values persist on the same tax rule via TaxSvc.SaveRule.
        var withholdingPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        withholdingPanel.Children.Add(CheckPanel("TaxSetup.Withholding", _withholding));
        withholdingPanel.Children.Add(FieldPanel("TaxSetup.WithholdingAccount", _withholdingAccount));
        withholdingPanel.Children.Add(FieldPanel("TaxSetup.WithholdingThreshold", _withholdingThreshold));
        var modePanel = new StackPanel { Margin = new Thickness(6, 0, 6, 0), MinWidth = 130 };
        modePanel.Children.Add(new TextBlock { Text = T("TaxSetup.WithholdingThresholdMode"), Tag = "TaxSetup.WithholdingThresholdMode" });
        _withholdingMode.ItemsSource = new[] { "PerDocument", "Cumulative" };
        _withholdingMode.SelectedIndex = 0;
        modePanel.Children.Add(_withholdingMode);
        withholdingPanel.Children.Add(modePanel);
        _withholdingAccount.Text = "2215";

        var content = new DockPanel();
        DockPanel.SetDock(accountPanel, Dock.Top);
        content.Children.Add(accountPanel);
        DockPanel.SetDock(withholdingPanel, Dock.Top);
        content.Children.Add(withholdingPanel);
        content.Children.Add(editor);

        var actions = new StackPanel { Orientation = Orientation.Horizontal,  Margin = new Thickness(0, 8, 0, 0) };
        FlowAlign.End(actions);
        ConfigureButton(_new, "Common.New");
        ConfigureButton(_save, "Common.Save");
        ConfigureButton(_refresh, "Common.Refresh");
        ConfigureButton(_close, "Common.Close");
        _new.Click += (_, _) => ClearEditor();
        _save.Click += (_, _) => SaveRule();
        _refresh.Click += (_, _) => RefreshData();
        _close.Click += (_, _) => Close();
        foreach (var button in new[] { _new, _save, _refresh, _close })
            actions.Children.Add(button);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        // Middle band: rules grid + editor in star rows, so at the
        // minimum the editor scrolls instead of clipping its last rows
        // and the rules grid keeps its share, exactly as at full size.
        var middle = new Grid();
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.8, GridUnitType.Star) });
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });
        Grid.SetRow(_rules, 0);
        middle.Children.Add(_rules);
        var editorScroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(editorScroll, 1);
        middle.Children.Add(editorScroll);
        root.Children.Add(middle);

        _country.SelectionChanged += (_, _) =>
        {
            if (_loadingCountry || !IsLoaded) return;
            var selectedCode = _country.SelectedValue?.ToString();
            var settings = TaxSvc.LoadSettings();
            if (settings is null || !settings.Enabled || string.Equals(settings.CountryCode, selectedCode, StringComparison.OrdinalIgnoreCase))
                return;
            _country.SelectedValue = settings.CountryCode;
            MessageBox.Show(this, T("TaxSetup.ChangeJurisdictionUseAction"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        };

        Content = root;
        RefreshPermissions();
    }

    private static void AddField(Grid grid, int row, int column, string key, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(new TextBlock { Text = T(key), Tag = key, Margin = new Thickness(0, 0, 0, 3) });
        control.Margin = new Thickness(0, 0, 0, 2);
        panel.Children.Add(control);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private static StackPanel FieldPanel(string key, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(6, 0, 6, 0), MinWidth = 130 };
        panel.Children.Add(new TextBlock { Text = T(key), Tag = key });
        panel.Children.Add(control);
        return panel;
    }

    private static StackPanel CheckPanel(string key, CheckBox check)
    {
        var panel = new StackPanel { Margin = new Thickness(6, 16, 6, 0) };
        check.Content = T(key);
        check.Tag = key;
        panel.Children.Add(check);
        return panel;
    }

    private static void ConfigureButton(Button button, string key)
    {
        button.Content = T(key);
        button.Tag = key;
        button.MinWidth = 82;
        button.Margin = new Thickness(4, 0, 0, 0);
        button.Padding = new Thickness(10, 5, 10, 5);
    }

    private void RefreshPermissions()
    {
        var canManage = AppHost.Permissions.Has(PermCat.Accounting.TaxManage);
        var taxEnabled = TaxSvc.IsEnabled;
        _save.IsEnabled = canManage && taxEnabled;
        _new.IsEnabled = canManage && taxEnabled;
        _jurisdictionAction.IsEnabled = canManage;
        _country.IsEnabled = canManage;
    }

    private void RefreshData()
    {
        var settings = TaxSvc.LoadSettings();
        _loadingCountry = true;
        try
        {
            _country.SelectedValue = settings?.CountryCode;
            _registrationNumber.Text = settings?.RegistrationNumber ?? string.Empty;
        }
        finally
        {
            _loadingCountry = false;
        }

        if (settings is null || !settings.Enabled)
        {
            _status.Text = T("TaxSetup.Disabled");
            _jurisdictionAction.Content = T("TaxSetup.Enable");
            _rules.ItemsSource = Array.Empty<TaxSvc.TaxRuleRec>();
            _jurisdictionAction.IsEnabled = AppHost.Permissions.Has(PermCat.Accounting.TaxManage);
        }
        else
        {
            var pack = TaxCountryPackCatalog.FindByNameOrCode(settings.CountryCode);
            var loadedRules = TaxSvc.LoadRules();
            var pendingReview = loadedRules.Count(rule =>
                string.Equals(rule.DataStatus, "BASELINE_REVIEW_REQUIRED", StringComparison.OrdinalIgnoreCase));
            var configuredRules = loadedRules.Count - pendingReview;
            var statusKey = loadedRules.Count == 0
                ? "TaxSetup.EnabledNeedsRules"
                : pendingReview > 0
                    ? "TaxSetup.EnabledReviewStatus"
                    : "TaxSetup.EnabledConfiguredStatus";
            _status.Text = string.Format(CultureInfo.CurrentCulture,
                T(statusKey), pack?.CountryName ?? settings.CountryCode, settings.Scheme,
                pendingReview, configuredRules);
            _jurisdictionAction.Content = T("TaxSetup.Disable");
            _rules.ItemsSource = loadedRules;
            _jurisdictionAction.IsEnabled = AppHost.Permissions.Has(PermCat.Accounting.TaxManage);
        }
        RefreshPermissions();
    }

    private void ChangeJurisdiction()
    {
        if (!AppHost.Permissions.Has(PermCat.Accounting.TaxManage))
        {
            MessageBox.Show(this, T("TaxSetup.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = TaxSvc.LoadSettings();
        if (settings is { Enabled: true })
        {
            var confirmDisable = MessageBox.Show(this, T("TaxSetup.DisableConfirm"), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirmDisable != MessageBoxResult.Yes) return;
            TaxSvc.SetTaxEnabled(settings.CountryCode, false, settings.Scheme, settings.RegistrationNumber);
            RefreshData();
            return;
        }

        if (_country.SelectedValue is not string countryCode || string.IsNullOrWhiteSpace(countryCode))
        {
            MessageBox.Show(this, T("TaxSetup.SelectCountry"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pack = TaxCountryPackCatalog.FindByNameOrCode(countryCode);
        var prompt = Tf("TaxSetup.EnableConfirm", pack?.CountryName ?? countryCode);
        var confirmEnable = MessageBox.Show(this, prompt, Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirmEnable != MessageBoxResult.Yes) return;

        var selectedPack = TaxCountryPackCatalog.FindByNameOrCode(countryCode);
        TaxSvc.SetTaxEnabled(countryCode, true, selectedPack?.Regime.ToString() ?? string.Empty,
            _registrationNumber.Text.Trim());
        RefreshData();
    }

    private void LoadSelectedRule()
    {
        if (_rules.SelectedItem is not TaxSvc.TaxRuleRec rule) return;
        _code.Text = rule.Code;
        _name.Text = rule.Name;
        _type.Text = rule.TaxType;
        _treatment.Text = rule.Treatment;
        _rate.Text = rule.Rate.ToString(CultureInfo.InvariantCulture);
        _calculation.SelectedItem = rule.CalculationMode;
        _effectiveFrom.SelectedDate = rule.EffectiveFrom;
        _effectiveTo.SelectedDate = rule.EffectiveTo;
        _outputAccount.Text = rule.OutputAccountCode;
        _inputAccount.Text = rule.InputAccountCode;
        _recoverable.IsChecked = rule.IsRecoverable;
        _compound.IsChecked = rule.IsCompound;
        _default.IsChecked = rule.IsDefault;
        _withholding.IsChecked = rule.IsWithholding;
        _withholdingAccount.Text = rule.WithholdingAccountCode;
        _withholdingThreshold.Text = rule.WithholdingThresholdAmount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _withholdingMode.SelectedItem = rule.WithholdingThresholdMode;
        _sourceUrl.Text = rule.SourceUrl;
        _sourceReference.Text = rule.SourceReference;
    }

    private void ClearEditor()
    {
        _rules.SelectedItem = null;
        _code.Clear();
        _name.Clear();
        _type.Text = TaxSvc.LoadSettings()?.Scheme ?? "VAT";
        _treatment.Text = "Standard";
        _rate.Clear();
        _calculation.SelectedItem = "Exclusive";
        _effectiveFrom.SelectedDate = DateTime.Today;
        _effectiveTo.SelectedDate = null;
        _outputAccount.Text = "2205";
        _inputAccount.Text = "1205";
        _recoverable.IsChecked = true;
        _compound.IsChecked = false;
        _default.IsChecked = false;
        _withholding.IsChecked = false;
        _withholdingAccount.Text = "2215";
        _withholdingThreshold.Clear();
        _withholdingMode.SelectedItem = "PerDocument";
        _sourceUrl.Clear();
        _sourceReference.Clear();
    }

    private void SaveRule()
    {
        try
        {
            if (_effectiveFrom.SelectedDate is not DateTime from)
                throw new InvalidOperationException(T("TaxSetup.EffectiveFromRequired"));
            if (!decimal.TryParse(_rate.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate))
                throw new InvalidOperationException(T("TaxSetup.InvalidRate"));

            decimal? withholdingThreshold = null;
            if (!string.IsNullOrWhiteSpace(_withholdingThreshold.Text))
            {
                if (!decimal.TryParse(_withholdingThreshold.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedThreshold))
                    throw new InvalidOperationException(T("TaxSetup.InvalidRate"));
                withholdingThreshold = parsedThreshold;
            }
            TaxSvc.SaveRule(new TaxSvc.TaxRuleDraft(
                _code.Text, _name.Text, _type.Text, _treatment.Text, rate,
                from, _effectiveTo.SelectedDate, _recoverable.IsChecked == true,
                _compound.IsChecked == true, _default.IsChecked == true,
                _outputAccount.Text, _inputAccount.Text,
                _calculation.SelectedItem?.ToString() ?? "Exclusive",
                _sourceUrl.Text.Trim(), _sourceReference.Text.Trim(),
                _withholding.IsChecked == true, _withholdingAccount.Text.Trim(),
                withholdingThreshold, _withholdingMode.SelectedItem?.ToString() ?? "PerDocument"));

            _status.Text = T("TaxSetup.Saved");
            RefreshData();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "TaxSetupWin.SaveRule");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
