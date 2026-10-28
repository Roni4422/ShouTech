using System;
using System.Windows;
using ShouTech.App.Services;
using ShouTech.App.Localization;
using ShouTech.App.ViewModels;

namespace ShouTech.App.Views;

public partial class PrmWin : Window
{
    private readonly PrmWinVM _vm;

    public PrmWin(string userId)
    {
        if (!AppHost.Permissions.Has("SYS.PERMISSIONS.MANAGE"))
            throw new UnauthorizedAccessException("You are not authorized to manage user permissions.");

        InitializeComponent();
        VisuId.ApplyWindow(this);
        _vm = new PrmWinVM(userId);
        DataContext = _vm;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            UiLoczr.ApplyToWindow(this);
            LangDirSv.ApplyToWindow(this, App.CurrentLanguageCode);
            await _vm.LoadAsync();
        }
        catch (Exception ex)
        {
            AppHost.Logging.LogWarning("PrmWin", ex.Message);
            MessageBox.Show(this, "تعذر تحميل الصلاحيات.", "ShouTech ERP", MessageBoxButton.OK, MessageBoxImage.Error);
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
            AppHost.Logging.LogWarning("PrmWin", ex.Message);
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
