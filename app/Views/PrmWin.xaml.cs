using System;
using System.Windows;
using ShouTech.App.Services;
using ShouTech.App.Localization;
using ShouTech.App.ViewModels;
using ShouTech.Domain;

namespace ShouTech.App.Views;

public partial class PrmWin : BaseWin, IWindowMode
{
    protected override string? ScheduleTintName => "Hickory";

    private readonly PrmWinVM _vm;

    /// <summary>Step 11 router identity (IWindowMode): the user whose
    /// permissions this window edits — an editor open for one user must
    /// never be reactivated for a request to edit another.</summary>
    public string ModeKey => _vm.UserId;

    /// <summary>BaseWin localization hook (the window migrated from
    /// Window to BaseWin in R5): the title comes from the catalog
    /// and the XAML's LocProps texts re-resolve through UiLoczr —
    /// on open and on every live language switch.</summary>
    protected override void ApplyLocalizedText()
    {
        Title = Loc.Get("Permissions.Title", "User permissions");
        UiLoczr.ApplyToWindow(this);
    }

    public PrmWin(string userId)
    {
        // No authorization throw in the constructor: a throwing ctor
        // surfaced as the global dispatcher dialog (sweep S-7/P-3).
        // The permission gate runs on Loaded below, with the same
        // deny-close pattern as the other guarded windows.
        InitializeComponent();
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
