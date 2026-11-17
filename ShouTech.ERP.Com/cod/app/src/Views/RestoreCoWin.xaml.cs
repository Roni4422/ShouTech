using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ShouTech.App.Configuration;
using ShouTech.App.Services;

using ShouTech.Domain;
namespace ShouTech.App.Views
{
    /// <summary>
    /// Lists soft-deleted companies and restores the selected one into DB + index.
    /// Opened from: Other Tools → Restore company.
    /// </summary>
    public partial class RestoreCoWin : Window
    {
        private readonly ObservableCollection<DeletedCompanyItem> _items = new();

        public RestoreCoWin()
        {
            InitializeComponent();
            VisuId.ApplyWindow(this);
            lstDeleted.ItemsSource = _items;
            ApplyLanguage();
            Loaded += async (_, _) => await ReloadAsync();
        }

        private void ApplyLanguage()
        {
            Title = ShouTech.App.Localization.Loc.Get("RestoreCompany.Title", "Restore a Company");
            txtTitle.Text = Title;
            txtHint.Text = ShouTech.App.Localization.Loc.Get("RestoreCompany.Hint", "Select a deleted company, then click Restore.");
            btnRefresh.Content = ShouTech.App.Localization.Loc.Get("RestoreCompany.Refresh", "Refresh");
            btnRestore.Content = ShouTech.App.Localization.Loc.Get("RestoreCompany.Restore", "Restore");
            btnClose.Content = ShouTech.App.Localization.Loc.Get("RestoreCompany.Close", "Close");
        }

        private async Task ReloadAsync()
        {
            _items.Clear();
            try
            {
                var deleted = await CoRegSvc.GetDeletedCompaniesAsync();
                foreach (var c in deleted.OrderBy(x => x.Name))
                {
                    _items.Add(new DeletedCompanyItem
                    {
                        Code = c.Code ?? string.Empty,
                        Name = c.Name ?? string.Empty,
                        NameEn = c.NameEn ?? c.Name ?? string.Empty
                    });
                }

                if (_items.Count == 0)
                {
                    var lang = ShouTech.App.App.CurrentLanguageCode ?? "ar";
                    MessageBox.Show(
                        ShouTech.App.Localization.Loc.Get("RestoreCompany.EmptyList", "No deleted companies to restore."),
                        Title,
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "RestoreCoWin.xaml.ReloadAsync");
                MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
            => await ReloadAsync();

        private async void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (lstDeleted.SelectedItem is not DeletedCompanyItem item)
            {
                MessageBox.Show(
                    ShouTech.App.Localization.Loc.Get("RestoreCompany.SelectCompany", "Please select a company."),
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                var ok = await CoRegSvc.RestoreCompanyAsync(item.Code);
                if (!ok)
                {
                    MessageBox.Show(
                        ShouTech.App.Localization.Loc.Get("RestoreCompany.RestoreDbFailed", "Could not restore the company from the database."),
                        Title,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                MessageBox.Show(
                    string.Format(CultureInfo.CurrentCulture, ShouTech.App.Localization.Loc.Get("RestoreCompany.RestoreOk", "\u201C{0}\u201D was restored and reindexed."), item.DisplayName),
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                await ReloadAsync();
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "RestoreCoWin.xaml.BtnRestore_Click");
                MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }

    public sealed class DeletedCompanyItem
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string NameEn { get; set; } = "";

        public string DisplayName
        {
            get
            {
                var lang = ShouTech.App.App.CurrentLanguageCode ?? "ar";
                if (lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrWhiteSpace(Name) ? (NameEn.Length > 0 ? NameEn : Code) : Name;
                return string.IsNullOrWhiteSpace(NameEn) ? (Name.Length > 0 ? Name : Code) : NameEn;
            }
        }
    }
}
