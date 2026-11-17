using System;
using System.Windows;
using ShouTech.App.Services;
using ShouTech.App.Localization;
using ShouTech.App.ViewModels;
using ShouTech.Domain;

namespace ShouTech.App.Views;

public partial class PrmWin : Window
{
    private readonly PrmWinVM _vm;

    public PrmWin(string userId)
    {
        // No authorization throw in the constructor: a throwing ctor
        // surfaced as the global dispatcher dialog (sweep S-7/P-3).
        // The permission gate runs on Loaded below, with the same
        // deny-close pattern as the other guarded windows.
        InitializeComponent();
        VisuId.ApplyWindow(this);
        _vm = new PrmWinVM(userId);
        DataContext = _vm;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.System.PermissionsManage))
        {
            AppHost.Errors.Report(ErrorCodes.PermissionDenied, null, "PrmWin.OnLoaded");
            MessageBox.Show(this,
                Loc.Get("Permissions.PermissionDenied", "You do not have permission to manage user permissions."),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        try
        {
            UiLoczr.ApplyToWindow(this);
            LangDirSv.ApplyToWindow(this, App.CurrentLanguageCode);
            await _vm.LoadAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowLoadFailed, ex, "PrmWin.OnLoaded");
            MessageBox.Show(this,
                Loc.Get("Permissions.LoadFailed", "Could not load permissions."),
                "ShouTech ERP", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
            Close();
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnSave.IsEnabled = false;
            await _vm.SaveAsync();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PrmWin.Save_Click");
            MessageBox.Show(this, ex.Message, "ShouTech ERP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { BtnSave.IsEnabled = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
