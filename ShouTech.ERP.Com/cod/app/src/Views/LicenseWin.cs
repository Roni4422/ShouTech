using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>
/// License window over AppHost.License (ILicenSvc). The API surface found
/// by inspection: Current (LicenseInfo: key, hardware id, expiry, activation
/// and last-check timestamps, status, offline mode), IsValid,
/// ValidateOffline() and TryOnlineValidationAsync(). The service exposes
/// NO renewal flow and NO license-key entry, so the Renew button is shown
/// disabled with an honest explanation rather than a fake flow; online
/// validation is real and wired.
/// </summary>
public sealed class LicenseWin : BaseWin
{
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
        MinHeight = 420;
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
        _renewButton.IsEnabled = false;
        ShowInfo();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_validateButton, "License.ValidateOnline");
        F2Ui.StyleButton(_renewButton, "License.Renew");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _validateButton.Click += async (_, _) => await ValidateOnlineAsync();
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
        _renewNote.Text = T("License.RenewNotSupported");
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
        _statusBar.Text = info.Status.ToString();
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
