using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ShouTech.App;
using ShouTech.App.Localization;
using ShouTech.App.Services;

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>
/// Company recycle bin for soft-deleted master data.
/// Restore returns rows to working sets; permanent delete is irreversible.
/// </summary>
public partial class BinWin : Window
{
    private bool _busy;

    // CA1863: cache CompositeFormat instances for repeatedly used format strings.
    private static readonly ConcurrentDictionary<string, CompositeFormat> CompositeFormats =
        new(StringComparer.Ordinal);

    public BinWin()
    {
        InitializeComponent();
        Win11Chrome.Apply(this);
        ApplyLanguage();
        Loaded += OnLoaded;
        try
        {
            LangDirSv.ApplyToWindow(this);
        }
        catch (Exception exL37)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL37, "BinWin.xaml.BinWin");
            // Direction helpers must not block the window.
        }
    }

    private static bool IsArabic => (App.CurrentLanguageCode ?? "en").StartsWith("ar", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Formats a localized pattern using a cached CompositeFormat
    /// to avoid re-parsing the format string on every call (CA1863).
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

    private void ApplyLanguage()
    {
        FlowDirection = IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Title = Loc.Get("RecycleBin.Title", "Recycle Bin");
        TitleText.Text = Title;
        HintText.Text = Loc.Get("RecycleBin.Description",
            "Soft-deleted master data for the active company. Restore returns items to working lists. Permanent delete cannot be undone.");
        DatasetColumn.Header = Loc.Get("RecycleBin.Dataset", "Dataset");
        LabelColumn.Header = Loc.Get("RecycleBin.Label", "Label");
        TableColumn.Header = Loc.Get("RecycleBin.Table", "Table");
        DeletedAtColumn.Header = Loc.Get("RecycleBin.DeletedAt", "Deleted at");
        DeletedByColumn.Header = Loc.Get("RecycleBin.DeletedBy", "Deleted by");
        RefreshButton.Content = Loc.Get("RecycleBin.Refresh", "Refresh");
        RestoreButton.Content = Loc.Get("RecycleBin.RestoreSelected", "Restore selected");
        PurgeButton.Content = Loc.Get("RecycleBin.DeletePermanently", "Delete permanently");
        CloseButton.Content = Loc.Get("RecycleBin.Close", "Close");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task ReloadAsync()
    {
        if (_busy)
            return;

        SetBusy(true);
        try
        {
            StatusText.Text = Loc.Get("RecycleBin.Loading", "Loading…");
            var companyCode = AppState.CurrentCompanyCode ?? string.Empty;
            if (string.IsNullOrWhiteSpace(companyCode))
            {
                ItemsGrid.ItemsSource = Array.Empty<RecycleBinItem>();
                StatusText.Text = Loc.Get("RecycleBin.NoActiveCompany", "No active company. Open a company session first.");
                return;
            }

            var items = await RecycleBinSvc.ListAsync(companyCode).ConfigureAwait(true);
            ItemsGrid.ItemsSource = items;
            StatusText.Text = items.Count == 0
                ? Loc.Get("RecycleBin.Empty", "The recycle bin is empty for this company.")
                : FormatLocalized(
                    Loc.Get("RecycleBin.ItemCount", "{0:N0} item(s) in the recycle bin for company {1}."),
                    items.Count,
                    companyCode);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BinWin.xaml.ReloadAsync");
            ItemsGrid.ItemsSource = Array.Empty<RecycleBinItem>();
            StatusText.Text = Loc.Get("RecycleBin.LoadFailed", "Failed to load the recycle bin.");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await ReloadAsync().ConfigureAwait(true);

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, Loc.Get("RecycleBin.SelectToRestore", "Select one or more rows to restore."), Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        try
        {
            var results = await RecycleBinSvc.RestoreManyAsync(selected).ConfigureAwait(true);
            var restored = results.Count(result => result.Succeeded);
            var failed = results.Count - restored;

            if (failed == 0)
            {
                MessageBox.Show(this,
                    FormatLocalized(Loc.Get("RecycleBin.Restored", "Restored {0:N0} item(s)."), restored),
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var firstError = results.FirstOrDefault(result => !result.Succeeded)?.ErrorMessage;
                MessageBox.Show(this,
                    FormatLocalized(
                        Loc.Get("RecycleBin.RestoredPartial", "Restored {0:N0} item(s). {1:N0} failed.{2}{3}"),
                        restored, failed, Environment.NewLine, firstError ?? string.Empty),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BinWin.xaml.Restore_Click");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    private async void Purge_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, Loc.Get("RecycleBin.SelectToPurge", "Select one or more rows to delete permanently."), Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            Loc.Get("RecycleBin.ConfirmPurge", "Selected items will be permanently deleted and cannot be restored.\n\nContinue?"),
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        try
        {
            var purged = 0;
            var failed = 0;
            string? firstError = null;
            foreach (var item in selected)
            {
                var result = await RecycleBinSvc.PurgeAsync(item).ConfigureAwait(true);
                if (result.Succeeded)
                    purged++;
                else
                {
                    failed++;
                    firstError ??= result.ErrorMessage;
                }
            }

            if (failed == 0)
            {
                MessageBox.Show(this,
                    FormatLocalized(Loc.Get("RecycleBin.Purged", "Permanently deleted {0:N0} item(s)."), purged),
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this,
                    FormatLocalized(
                        Loc.Get("RecycleBin.PurgedPartial", "Permanently deleted {0:N0} item(s). {1:N0} failed.{2}{3}"),
                        purged, failed, Environment.NewLine, firstError ?? string.Empty),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BinWin.xaml.Purge_Click");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private List<RecycleBinItem> GetSelectedItems()
        => ItemsGrid.SelectedItems.OfType<RecycleBinItem>().ToList();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy;
        PurgeButton.IsEnabled = !busy;
        ItemsGrid.IsEnabled = !busy;
    }
}