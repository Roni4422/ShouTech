using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ShouTech.App.Configuration;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Updates window — deliberately honest. It shows the installed version
/// (DeviceInfo + application configuration). The update source is the
/// configured activation/update server (Licensing.ActivationServer); the
/// shipped configuration leaves it empty, in which case the window states
/// "not configured" and Check does nothing network-related. When a source
/// IS configured, Check performs a real HTTP HEAD (GET fallback) against
/// {source}/version.json with a timeout and reports the actual result.
/// Update from file: the user picks a package file; the window verifies it
/// exists, shows its size and SHA-256 hash, and records the attempt in the
/// audit trail. It does NOT install anything and says so.
/// </summary>
public sealed class UpdatesWin : BaseWin
{
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly TextBlock _versionInfo = new();
    private readonly TextBlock _sourceInfo = new();
    private readonly TextBlock _fileInfo = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _checkButton = new();
    private readonly Button _fileButton = new();
    private readonly Button _closeButton = new();

    public UpdatesWin()
    {
        Width = 720;
        Height = 480;
        MinWidth = 600;
        MinHeight = 380;
        BuildUi();
        Loaded += OnLoaded;
    }

    private static string? UpdateSource
    {
        get
        {
            var server = AppCfgSvc.Current?.Licensing?.Activation?.ActivationServer;
            return string.IsNullOrWhiteSpace(server) ? null : server.Trim().TrimEnd('/');
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Updates.Check))
        {
            MessageBox.Show(this, T("Updates.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        ShowState();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_checkButton, "Updates.Check");
        F2Ui.StyleButton(_fileButton, "Updates.FromFile");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _checkButton.Click += async (_, _) => await CheckAsync();
        _fileButton.Click += async (_, _) => await FromFileAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_checkButton, _fileButton, _closeButton));

        var content = new StackPanel();
        foreach (var block in new[] { _versionInfo, _sourceInfo, _fileInfo })
        {
            block.TextWrapping = TextWrapping.Wrap;
            block.Margin = new Thickness(0, 0, 0, 10);
            content.Children.Add(block);
        }
        root.Children.Add(content);

        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Updates.Title");
        foreach (var b in new[] { _checkButton, _fileButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        ShowState();
    }

    private void ShowState()
    {
        var device = AppHost.DeviceInfo?.Current;
        var appVersion = AppCfgSvc.Current?.Application?.Version ?? device?.AppVersion ?? string.Empty;
        _versionInfo.Text = $"{T("Updates.CurrentVersion")}: {appVersion}" +
            (device is null ? string.Empty : $"  ·  {device.MachineName}  ·  {device.OsVersion}");
        var source = UpdateSource;
        _sourceInfo.Text = source is null
            ? T("Updates.NotConfigured")
            : $"{T("Updates.Source")}: {source}/version.json";
        _checkButton.IsEnabled = source is not null && AppHost.Permissions.Has(PermCat.Updates.Check);
    }

    private async Task CheckAsync()
    {
        try
        {
            var source = UpdateSource;
            if (source is null)
            {
                _statusBar.Text = T("Updates.NotConfigured");
                return;
            }
            var url = source + "/version.json";
            _checkButton.IsEnabled = false;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                HttpResponseMessage response;
                try
                {
                    response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                    if ((int)response.StatusCode >= 400)
                        response = await http.GetAsync(url);
                }
                catch (HttpRequestException)
                {
                    response = await http.GetAsync(url);
                }
                using (response)
                {
                    var body = response.IsSuccessStatusCode
                        ? await response.Content.ReadAsStringAsync()
                        : string.Empty;
                    _statusBar.Text = $"{url} -> {(int)response.StatusCode} {response.ReasonPhrase}";
                    _fileInfo.Text = string.IsNullOrWhiteSpace(body)
                        ? _statusBar.Text
                        : $"{_statusBar.Text}{Environment.NewLine}{Truncate(body, 400)}";
                }
            }
            finally
            {
                _checkButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _statusBar.Text = ex.Message;
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task FromFileAsync()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Updates.ManualInstall))
                throw new InvalidOperationException(T("Updates.PermissionDenied"));
            var dialog = new OpenFileDialog
            {
                Filter = "Update packages (*.zip;*.stei;*.exe;*.msi)|*.zip;*.stei;*.exe;*.msi|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true)
                return;

            var info = new FileInfo(dialog.FileName);
            string hash;
            await using (var stream = info.OpenRead())
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));

            _fileInfo.Text = $"{info.FullName}{Environment.NewLine}" +
                             $"{T("Updates.FileSize")}: {info.Length:N0} bytes{Environment.NewLine}" +
                             $"SHA-256: {hash}{Environment.NewLine}" +
                             T("Updates.NotInstalledNote");
            _statusBar.Text = info.Name;

            await SecurityAuditLog.WriteAsync(CurrentCompanyCode, null, CurrentUserId,
                "UPDATE_FILE_VERIFIED", $"Manual update package verified (not installed): {info.Name}, {info.Length} bytes, SHA-256 {hash}.");
            if (AppHost.Audit is { } audit)
                await audit.LogUserActionAsync(CurrentUserId, "UpdateFileVerified", info.Name);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "...";
}
