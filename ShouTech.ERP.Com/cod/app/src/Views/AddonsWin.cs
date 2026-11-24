using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Domain.Enums;

namespace ShouTech.App.Views;

/// <summary>
/// Program add-ons window: the module catalogue from the application
/// configuration (Modules.AllModules + Modules.Enabled) with each module's
/// version, under the current license status. Licensing build: the
/// Enabled state is no longer the config flag alone — a feature that
/// maps to a gated license module (POS, Manufacturing, Human
/// Resources, CRM, AI) is enabled only when the live entitlement
/// snapshot (AppHost.Entitlements) grants it, and core features
/// require a usable license. There is still NO API to enable/disable
/// modules at runtime, so the list is read-only and the window says
/// so — no fake switches.
/// </summary>
public sealed class AddonsWin : BaseWin
{
    protected override string? ScheduleTintName => "Wheat";
    public sealed class AddonRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public bool Required { get; set; }
    }

    private readonly List<AddonRow> _rows = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly TextBlock _note = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    public AddonsWin()
    {
        Width = 860;
        Height = 540;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.System.PluginsManage))
        {
            MessageBox.Show(this, T("Addons.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        RefreshList();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _refreshButton.Click += (_, _) => RefreshList();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_refreshButton, _closeButton));

        var content = new DockPanel();
        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(_note, Dock.Top);
        content.Children.Add(_note);

        F2Ui.ConfigureGrid(_grid);
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Addons.Id", nameof(AddonRow.Id), 120));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Addons.Name", nameof(AddonRow.Name), 260));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Addons.Version", nameof(AddonRow.Version), 100, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Addons.Enabled", nameof(AddonRow.Enabled), 90, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Addons.Required", nameof(AddonRow.Required), 90, center: true));
        content.Children.Add(_grid);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Addons.Title");
        _note.Text = T("Addons.ReadOnlyNote");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _refreshButton, _closeButton }, T);
    }

    /// <summary>Config feature id → gated license module, for the
    /// features the license vocabulary gates. Features absent here
    /// belong to the core bundle (usable license required, no module
    /// grant).</summary>
    private static readonly Dictionary<string, string> FeatureLicenseModules =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["POS"] = ModKey.Pos,
            ["Manufacturing"] = ModKey.Mfg,
            ["HumanResources"] = ModKey.Hr,
            ["CRM"] = ModKey.Crm,
            ["AI"] = ModKey.Ai
        };

    private void RefreshList()
    {
        _rows.Clear();
        var modules = AppCfgSvc.Current?.Modules;
        var enabled = new HashSet<string>(modules?.Enabled ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var entitlements = AppHost.Entitlements;
        var snapshot = entitlements?.Current;
        foreach (var module in modules?.AllModules ?? new List<ModuleInfo>())
        {
            var configEnabled = module.Enabled || enabled.Contains(module.Id);
            bool licensed;
            if (entitlements is null || snapshot is null)
                licensed = false;
            else if (FeatureLicenseModules.TryGetValue(module.Id, out var licenseModule))
                licensed = entitlements.IsEntitled(licenseModule);
            else
                licensed = snapshot.IsUsable;
            _rows.Add(new AddonRow
            {
                Id = module.Id,
                Name = string.IsNullOrWhiteSpace(module.NameEn) ? module.Name : module.NameEn,
                Version = module.Version,
                Enabled = configEnabled && licensed,
                Required = module.Required
            });
        }
        _grid.ItemsSource = null;
        _grid.ItemsSource = _rows;
        var licenseStatus = snapshot is null
            ? (AppHost.License?.Current?.Status.ToString() ?? "-")
            : $"{snapshot.Status} · {(string.IsNullOrWhiteSpace(snapshot.Edition) ? "-" : snapshot.Edition)} · {snapshot.Source}";
        _statusBar.Text = $"{_rows.Count} · {T("Addons.LicenseStatus")}: {licenseStatus} · {T("Addons.LicensedFeatures")}: {(snapshot?.Modules.Count ?? 0)}";
    }
}
