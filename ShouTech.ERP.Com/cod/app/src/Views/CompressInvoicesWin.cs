using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.Application.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Invoice compression window: previews how many soft-deleted DRAFT
/// documents exist per kind (sales / purchase, is_deleted = 1 AND
/// status = 'Draft'), then — after an explicit confirmation showing the
/// counts — hard-deletes those documents and their lines in ONE transaction
/// with a security_audit_log record (OperationsService). Posted documents
/// can never match the predicate and are never touched.
/// </summary>
public sealed class CompressInvoicesWin : BaseWin
{
    private readonly OperationsService? _service;
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly TextBlock _salesCount = new();
    private readonly TextBlock _purchaseCount = new();
    private readonly TextBlock _totalCount = new();
    private readonly TextBlock _note = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _compressButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private int _total;

    // CA1863: cache CompositeFormat instances for repeatedly used format strings.
    private static readonly ConcurrentDictionary<string, CompositeFormat> CompositeFormats =
        new(StringComparer.Ordinal);

    public CompressInvoicesWin()
    {
        _service = AppHost.Operations;
        Width = 640;
        Height = 430;
        MinWidth = 540;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Formats a localized pattern using a cached CompositeFormat
    /// to avoid re-parsing the format string on every call (CA1863),
    /// with the current UI culture applied explicitly (CA1305).
    /// </summary>
    private static string FormatLocalized(string pattern, params object?[] args)
    {
        if (string.IsNullOrEmpty(pattern))
            return string.Empty;

        var compositeFormat = CompositeFormats.GetOrAdd(
            pattern,
            static value => CompositeFormat.Parse(value));

        return string.Format(CultureInfo.CurrentCulture, compositeFormat, args);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.System.Maintenance))
        {
            MessageBox.Show(this, T("Compress.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_compressButton, "Compress.Compress");
        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _compressButton.Click += async (_, _) => await CompressAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_compressButton, _refreshButton, _closeButton));

        var content = new StackPanel();
        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(0, 0, 0, 14);
        content.Children.Add(_note);
        foreach (var block in new[] { _salesCount, _purchaseCount, _totalCount })
        {
            block.FontSize = 15;
            block.Margin = new Thickness(0, 0, 0, 6);
            content.Children.Add(block);
        }
        _totalCount.FontWeight = FontWeights.SemiBold;
        root.Children.Add(content);

        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Compress.Title");
        _note.Text = T("Compress.Note");
        foreach (var b in new[] { _compressButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        UpdateCounts(null);
    }

    private void UpdateCounts(ShouTech.Domain.Entities.CompressCounts? counts)
    {
        if (counts is null)
            return;
        _total = counts.Total;
        _salesCount.Text = $"{T("Compress.SalesDrafts")}: {counts.SalesDrafts}";
        _purchaseCount.Text = $"{T("Compress.PurchaseDrafts")}: {counts.PurchaseDrafts}";
        _totalCount.Text = $"{T("Compress.Total")}: {counts.Total}";
        _compressButton.IsEnabled = counts.Total > 0;
        _statusBar.Text = $"{counts.Total}";
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("Compress.ServiceUnavailable");
                return;
            }
            UpdateCounts(await _service.GetCompressCountsAsync(CurrentCompanyCode));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CompressAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Compress.ServiceUnavailable"));
            if (_total <= 0)
                throw new InvalidOperationException(T("Compress.NothingToCompress"));
            var confirm = FormatLocalized(T("Compress.Confirm"), _total);
            if (MessageBox.Show(this, confirm, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            var deleted = await _service.CompressDeletedDraftsAsync(CurrentCompanyCode, CurrentUserId);
            MessageBox.Show(this,
                FormatLocalized(T("Compress.Done"), deleted),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
