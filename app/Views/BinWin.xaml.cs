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
/// Company recycle bin: soft-deleted documents, journal entries and
/// cards for the active company (SOFT_DELETE_STUDY).
/// Restore returns rows to working sets; permanent delete is irreversible.
/// </summary>
public partial class BinWin : BaseWin
{
    protected override string? ScheduleTintName => "Bronze";
    private bool _busy;
    private IReadOnlyList<RecycleBinItem> _allItems = Array.Empty<RecycleBinItem>();

    // CA1863: cache CompositeFormat instances for repeatedly used format strings.
    private static readonly ConcurrentDictionary<string, CompositeFormat> CompositeFormats =
        new(StringComparer.Ordinal);

    public BinWin()
    {
        InitializeComponent();
        // Step 12: BinWin derives from BaseWin like every other window,
        // so the central policy reaches it — Win11 chrome, shell context
        // (language/direction/company/theme), geometry persistence and
        // the work-area clamp are all applied by the base class. The old
        // hand-rolled Win11Chrome.Apply + LangDirSv calls are gone: they
        // were the local copies of exactly what BaseWin does centrally.
        ApplyLanguage();
        Loaded += OnLoaded;
    }

    /// <summary>BaseWin relocalization hook: the base class calls this
    /// on load and again on every live language switch, so the recycle
    /// bin now follows the shell language like every other window.</summary>
    protected override void ApplyLocalizedText() => ApplyLanguage();

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
        // Direction is applied centrally by BaseWin (ShellContext via
        // the base Loaded path + the App Loaded class handler + the
        // live-switch sweep); this method only refreshes texts.
        Title = Loc.Get("RecycleBin.Title", "Recycle Bin");
        TitleText.Text = Title;
        HintText.Text = Loc.Get("RecycleBin.Description",
            "Soft-deleted master data for the active company. Restore returns items to working lists. Permanent delete cannot be undone.");
        DatasetColumn.Header = Loc.Get("RecycleBin.Dataset", "Dataset");
        LabelColumn.Header = Loc.Get("RecycleBin.Label", "Label");
        StatusColumn.Header = Loc.Get("RecycleBin.Status", "Status");
        TableColumn.Header = Loc.Get("RecycleBin.Table", "Table");
        DeletedAtColumn.Header = Loc.Get("RecycleBin.DeletedAt", "Deleted at");
        DeletedByColumn.Header = Loc.Get("RecycleBin.DeletedBy", "Deleted by");
        FilterDatasetLabel.Text = Loc.Get("RecycleBin.Dataset", "Dataset");
        FilterByLabel.Text = Loc.Get("RecycleBin.DeletedBy", "Deleted by");
        FilterFromLabel.Text = Loc.Get("RecycleBin.FilterFrom", "From");
        FilterToLabel.Text = Loc.Get("RecycleBin.FilterTo", "To");
        FilterButton.Content = Loc.Get("RecycleBin.ApplyFilter", "Filter");
        ClearFilterButton.Content = Loc.Get("RecycleBin.ClearFilter", "Clear");
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
            _allItems = items;
            RebuildDatasetFilter(items);
            ApplyFilters();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BinWin.xaml.ReloadAsync");
            _allItems = Array.Empty<RecycleBinItem>();
            ItemsGrid.ItemsSource = Array.Empty<RecycleBinItem>();
            StatusText.Text = Loc.Get("RecycleBin.LoadFailed", "Failed to load the recycle bin.");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Dataset filter choices: "All" plus the datasets present
    /// in the current bin content; the selection survives reloads.</summary>
    private void RebuildDatasetFilter(IReadOnlyList<RecycleBinItem> items)
    {
        var selected = FilterDatasetBox.SelectedItem as string;
        var choices = new List<string> { Loc.Get("RecycleBin.FilterAll", "All") };
        choices.AddRange(items.Select(item => item.DatasetKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.CurrentCultureIgnoreCase));
        FilterDatasetBox.ItemsSource = choices;
        FilterDatasetBox.SelectedItem = selected is not null && choices.Contains(selected)
            ? selected
            : choices[0];
    }

    /// <summary>Client-side filters over the loaded bin: dataset,
    /// deleted-by (contains), and the deletion date range.</summary>
    private void ApplyFilters()
    {
        var companyCode = AppState.CurrentCompanyCode ?? string.Empty;
        IEnumerable<RecycleBinItem> view = _allItems;

        var dataset = FilterDatasetBox.SelectedItem as string;
        var allLabel = Loc.Get("RecycleBin.FilterAll", "All");
        if (!string.IsNullOrEmpty(dataset) && !string.Equals(dataset, allLabel, StringComparison.Ordinal))
            view = view.Where(item => string.Equals(item.DatasetKey, dataset, StringComparison.OrdinalIgnoreCase));

        var by = FilterByBox.Text?.Trim();
        if (!string.IsNullOrEmpty(by))
            view = view.Where(item => (item.DeletedBy ?? string.Empty)
                .Contains(by, StringComparison.OrdinalIgnoreCase));

        var from = FilterFromPicker.SelectedDate?.Date;
        var to = FilterToPicker.SelectedDate?.Date;
        if (from is not null || to is not null)
            view = view.Where(item =>
            {
                if (!TryDeletedDate(item, out var date)) return false;
                if (from is not null && date < from.Value) return false;
                if (to is not null && date > to.Value) return false;
                return true;
            });

        var list = view.ToList();
        ItemsGrid.ItemsSource = list;
        StatusText.Text = _allItems.Count == 0
            ? Loc.Get("RecycleBin.Empty", "The recycle bin is empty for this company.")
            : FormatLocalized(
                Loc.Get("RecycleBin.ItemCount", "{0:N0} item(s) in the recycle bin for company {1}."),
                list.Count,
                companyCode);
    }

    private static bool TryDeletedDate(RecycleBinItem item, out DateTime date)
    {
        date = default;
        var text = item.DeletedAt;
        if (string.IsNullOrWhiteSpace(text) || text.Length < 10)
            return false;
        return DateTime.TryParseExact(text[..10], "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private void Filter_Click(object sender, RoutedEventArgs e) => ApplyFilters();

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        FilterByBox.Text = string.Empty;
        FilterFromPicker.SelectedDate = null;
        FilterToPicker.SelectedDate = null;
        if (FilterDatasetBox.Items.Count > 0)
            FilterDatasetBox.SelectedIndex = 0;
        ApplyFilters();
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
        // Permanent purge is irreversible destruction of master data:
        // administrator-only (SYS.ADMIN is outside every non-admin
        // role baseline). Restore above stays ungated — it is the
        // non-destructive direction.
        if (!AppHost.Permissions.Has(PermCat.System.Admin))
        {
            AppHost.Errors.Report(ErrorCodes.PermissionDenied, null, "BinWin.xaml.Purge_Click");
            MessageBox.Show(this,
                Loc.Get("RecycleBin.PermissionDenied", "You do not have permission to permanently delete items. This action requires an administrator (SYS.ADMIN)."),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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