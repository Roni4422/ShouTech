using System.Windows.Controls;
using ShouTech.App.Common;
using ShouTech.App.Localization;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Modules
{
    public partial class DashVw : UserControl
    {
        public DashVw()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            UiLoczr.ApplyToRoot(this);

            try
            {
                var data = AppHost.Data;

                CustomersCount.Text =
                    AppFormat.FormatNumber(await data.GetTotalCustomersAsync());

                ProductsCount.Text =
                    AppFormat.FormatNumber(await data.GetTotalProductsAsync());

                PendingCount.Text =
                    AppFormat.FormatNumber(await data.GetTotalPendingApprovalsAsync());
            }
            catch
            {
                CustomersCount.Text = "—";
                ProductsCount.Text = "—";
                PendingCount.Text = "—";
            }
        }
    }
}