using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Views;

/// <summary>
/// License window: the installation's real license surface.
/// Top section: the legacy online-activation service (AppHost.License,
/// ILicenSvc — key, hardware id, expiry, online validation).
/// Licensing build: the entitlement section below it is the live
/// gate's snapshot (AppHost.Entitlements — status, edition, source,
/// modules, dongle binding, device fingerprint, expiry/grace, demo
/// state) and the Import button installs a vendor-signed license
/// file: the file is verified BEFORE it may replace the current one,
/// and a file that does not verify is refused with LIC-8007.
/// </summary>
public sealed class LicenseWin : BaseWin
{
    protected override string? ScheduleTintName => "Jasper";
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly ListBox _info = new();
    private readonly TextBlock _renewNote = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _validateButton = new();
    private readonly Button _renewButton = new();
    private readonly Button _closeButton = new();

    public LicenseWin()
    {
        Width = 720;
        Height = 520;
        MinWidth = 600;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Licensing.View))
        {
            MessageBox.Show(this, T("License.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        _renewButton.IsEnabled = true;
        ShowInfo();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_validateButton, "License.ValidateOnline");
        F2Ui.StyleButton(_renewButton, "License.Import");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _validateButton.Click += async (_, _) => await ValidateOnlineAsync();
        _renewButton.Click += async (_, _) => await ImportLicenseAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_validateButton, _renewButton, _closeButton));

        var content = new DockPanel();
        _renewNote.TextWrapping = TextWrapping.Wrap;
        _renewNote.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(_renewNote, Dock.Top);
        content.Children.Add(_renewNote);
        content.Children.Add(_info);
        root.Children.Add(content);

        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("License.Title");
        foreach (var b in new[] { _validateButton, _renewButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
    }

    private void ShowInfo()
    {
        _info.Items.Clear();
        var license = AppHost.License;
        if (license is null)
        {
            _info.Items.Add(T("License.ServiceUnavailable"));
            return;
        }
        var info = license.Current;
        var device = AppHost.DeviceInfo?.Current;
        _info.Items.Add($"{T("License.Status")}: {info.Status}");
        _info.Items.Add($"{T("License.IsValid")}: {license.IsValid}");
        _info.Items.Add($"{T("License.Key")}: {MaskKey(info.LicenseKey)}");
        _info.Items.Add($"{T("License.HardwareId")}: {info.HardwareId}");
        _info.Items.Add($"{T("License.Expiry")}: {(info.ExpiryDate == DateTime.MaxValue ? "-" : info.ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture))}");
        _info.Items.Add($"{T("License.ActivatedAt")}: {(info.ActivatedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "-")}");
        _info.Items.Add($"{T("License.LastCheck")}: {(info.LastOnlineCheck?.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "-")}");
        _info.Items.Add($"{T("License.OfflineMode")}: {info.IsOfflineMode}");
        _info.Items.Add($"{T("License.AllowsOffline")}: {license.AllowsOfflineOperation}");
        if (device is not null)
        {
            _info.Items.Add($"{T("License.Device")}: {device.MachineName} ({device.DeviceId})");
            _info.Items.Add($"{T("License.AppVersion")}: {device.AppVersion}");
        }

        // ---- Licensing build: the live entitlement snapshot ----
        var entitlements = AppHost.Entitlements;
        if (entitlements is not null)
        {
            var snapshot = entitlements.Current;
            _info.Items.Add("—");
            _info.Items.Add($"{T("License.Entitlements")}: {snapshot.Status}");
            _info.Items.Add($"{T("License.Edition")}: {(string.IsNullOrWhiteSpace(snapshot.Edition) ? "-" : snapshot.Edition)}");
            _info.Items.Add($"{T("License.Source")}: {snapshot.Source}");
            _info.Items.Add($"{T("License.Customer")}: {snapshot.CustomerName ?? "-"}");
            _info.Items.Add($"{T("License.LicenseId")}: {snapshot.LicenseId ?? "-"}");
            _info.Items.Add($"{T("License.Modules")}: {(snapshot.Modules.Count == 0 ? "-" : string.Join(", ", snapshot.Modules.OrderBy(m => m, StringComparer.Ordinal)))}");
            _info.Items.Add($"{T("License.Dongle")}: {snapshot.DongleSerial ?? "-"}");
            _info.Items.Add($"{T("License.Fingerprint")}: {CollectFingerprintShortId()}");
            _info.Items.Add($"{T("License.Expiry")}: {(snapshot.ExpiresAt?.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) ?? "-")}");
            _info.Items.Add($"{T("License.GraceEnds")}: {(snapshot.GraceEndsAt?.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) ?? "-")}");
            _info.Items.Add($"{T("License.MaxUsers")}: {(snapshot.MaxUsers?.ToString(CultureInfo.CurrentCulture) ?? "-")} · {T("License.MaxCompanies")}: {(snapshot.MaxCompanies?.ToString(CultureInfo.CurrentCulture) ?? "-")}");
            _renewNote.Text = entitlements.IsDemoMode ? T("License.ViewOnly") : string.Empty;
            _statusBar.Text = entitlements.IsDemoMode
                ? $"{snapshot.Status} · {T("License.DemoActive")}"
                : snapshot.Status.ToString();
            return;
        }

        _statusBar.Text = info.Status.ToString();
    }

    private static string CollectFingerprintShortId()
    {
        try
        {
            return new FingerprintSvc().Collect().ShortId;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.LicenseCheckFailed, ex, "LicenseWin.Fingerprint");
            return "-";
        }
    }

    private async Task ImportLicenseAsync()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Licensing.Renew))
                throw new InvalidOperationException(T("License.PermissionDenied"));
            if (AppHost.Entitlements is null)
                throw new InvalidOperationException(T("License.ServiceUnavailable"));

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "ShouTech license (*.dat)|*.dat|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true)
                return;

            _renewButton.IsEnabled = false;
            try
            {
                await AppHost.Entitlements.ImportLicenseFileAsync(dialog.FileName);
            }
            finally
            {
                _renewButton.IsEnabled = true;
            }

            ShowInfo();
            _statusBar.Text = T("License.ImportDone");
        }
        catch (AppError appError)
        {
            // Cataloged refusal (e.g. LIC-8007 signature invalid):
            // show the catalog's localized message for the code.
            var info = AppHost.Errors.Resolve(appError.Code);
            MessageBox.Show(this, info.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.LicenseCheckFailed, ex, "LicenseWin.ImportLicense");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ValidateOnlineAsync()
    {
        try
        {
            if (AppHost.License is null)
                throw new InvalidOperationException(T("License.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Licensing.Renew) && !AppHost.Permissions.Has(PermCat.Licensing.View))
                throw new InvalidOperationException(T("License.PermissionDenied"));
            _validateButton.IsEnabled = false;
            try
            {
                var status = await AppHost.License.TryOnlineValidationAsync();
                _statusBar.Text = status.ToString();
            }
            finally
            {
                _validateButton.IsEnabled = true;
            }
            ShowInfo();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "-";
        var trimmed = key.Trim();
        return trimmed.Length <= 8
            ? "****"
            : trimmed[..4] + "-****-****-" + trimmed[^4..];
    }
}
