using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ShouTech.App;
using ShouTech.App.Services;

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
        ApplyLanguage();
        Loaded += OnLoaded;
        try
        {
            LangDirSv.ApplyToWindow(this);
        }
        catch
        {
            // Direction helpers must not block the window.
        }
    }

    private static bool IsArabic => (App.CurrentLanguageCode ?? "en").StartsWith("ar", StringComparison.OrdinalIgnoreCase);
    private static string L(string en, string ar) => IsArabic ? ar : en;

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
        Title = L("Recycle Bin", "سلة المحذوفات");
        TitleText.Text = Title;
        HintText.Text = L("Soft-deleted master data for the active company. Restore returns items to working lists. Permanent delete cannot be undone.",
            "البيانات الرئيسية المحذوفة للشركة الحالية. تعيد الاستعادة العناصر إلى القوائم، ولا يمكن التراجع عن الحذف النهائي.");
        DatasetColumn.Header = L("Dataset", "مجموعة البيانات");
        LabelColumn.Header = L("Label", "الاسم");
        TableColumn.Header = L("Table", "الجدول");
        DeletedAtColumn.Header = L("Deleted at", "تاريخ الحذف");
        DeletedByColumn.Header = L("Deleted by", "حُذف بواسطة");
        RefreshButton.Content = L("Refresh", "تحديث");
        RestoreButton.Content = L("Restore selected", "استعادة المحدد");
        PurgeButton.Content = L("Delete permanently", "حذف نهائي");
        CloseButton.Content = L("Close", "إغلاق");
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
            StatusText.Text = L("Loading…", "جارٍ التحميل…");
            var companyCode = AppState.CurrentCompanyCode ?? string.Empty;
            if (string.IsNullOrWhiteSpace(companyCode))
            {
                ItemsGrid.ItemsSource = Array.Empty<RecycleBinItem>();
                StatusText.Text = L("No active company. Open a company session first.", "لا توجد شركة نشطة. افتح جلسة شركة أولاً.");
                return;
            }

            var items = await RecycleBinSvc.ListAsync(companyCode).ConfigureAwait(true);
            ItemsGrid.ItemsSource = items;
            StatusText.Text = items.Count == 0
                ? L("The recycle bin is empty for this company.", "سلة المحذوفات فارغة لهذه الشركة.")
                : FormatLocalized(
                    L("{0:N0} item(s) in the recycle bin for company {1}.", "عدد العناصر في سلة المحذوفات: {0:N0}، الشركة: {1}."),
                    items.Count,
                    companyCode);
        }
        catch (Exception ex)
        {
            ItemsGrid.ItemsSource = Array.Empty<RecycleBinItem>();
            StatusText.Text = L("Failed to load the recycle bin.", "تعذر تحميل سلة المحذوفات.");
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
            MessageBox.Show(this, L("Select one or more rows to restore.", "حدد صفاً واحداً أو أكثر لاستعادتها."), Title,
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
                    FormatLocalized(L("Restored {0:N0} item(s).", "تمت استعادة {0:N0} عنصر."), restored),
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var firstError = results.FirstOrDefault(result => !result.Succeeded)?.ErrorMessage;
                MessageBox.Show(this,
                    FormatLocalized(
                        L("Restored {0:N0} item(s). {1:N0} failed.{2}{3}", "تمت استعادة {0:N0} عنصر، وفشلت استعادة {1:N0}.{2}{3}"),
                        restored, failed, Environment.NewLine, firstError ?? string.Empty),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
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
            MessageBox.Show(this, L("Select one or more rows to delete permanently.", "حدد صفاً واحداً أو أكثر للحذف النهائي."), Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            L("Selected items will be permanently deleted and cannot be restored.\n\nContinue?", "سيتم حذف العناصر المحددة نهائياً ولن يمكن استعادتها.\n\nهل تريد المتابعة؟"),
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
                    FormatLocalized(L("Permanently deleted {0:N0} item(s).", "تم حذف {0:N0} عنصر نهائياً."), purged),
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this,
                    FormatLocalized(
                        L("Permanently deleted {0:N0} item(s). {1:N0} failed.{2}{3}", "تم حذف {0:N0} عنصر نهائياً، وفشل حذف {1:N0}.{2}{3}"),
                        purged, failed, Environment.NewLine, firstError ?? string.Empty),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
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