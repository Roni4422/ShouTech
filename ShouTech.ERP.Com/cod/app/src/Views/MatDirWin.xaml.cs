using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShouTech.App.Localization;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

public sealed class MatDirListItem
{
public enum Kind
{
Material,
Group
}


public Kind ItemKind { get; init; }

public long Id { get; init; }

public string Code { get; init; } = "";

public string Name { get; init; } = "";

public string NameEn { get; init; } = "";

public string Status { get; init; } = "Active";

public string DisplayName
{
    get
    {
        var lang = App.CurrentLanguageCode ?? "en";

        if (lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(Name) ? NameEn : Name;

        return string.IsNullOrWhiteSpace(NameEn) ? Name : NameEn;
    }
}

public string StatusLabel =>
    string.Equals(Status, "Inactive", StringComparison.OrdinalIgnoreCase)
        ? Loc.Get("MatCard.StatusInactive", "Inactive")
        : Loc.Get("MatCard.StatusActive", "Active");


}

/// <summary>
/// Material and group directory.
/// </summary>
public partial class MatDirWin : BaseWin
{
private readonly ObservableCollection<MatDirListItem> _all = new();
private readonly ObservableCollection<MatDirListItem> _view = new();


private bool _modeItem = true;

public MatDirWin()
{
    InitializeComponent();
    VisuId.ApplyWindow(this);

    AppHost.Localization.LanguageChanged += OnLanguageChanged;

    ApplyDirection();

    LvItems.ItemsSource = _view;

    Reload();
}

private void ApplySizeFromOwner()
{
    if (Owner == null || Owner.ActualWidth <= 0 || Owner.ActualHeight <= 0)
        return;

    Width = Math.Max(
        MinWidth,
        Owner.ActualWidth * 0.45);

    Height = Math.Max(
        MinHeight,
        Owner.ActualHeight * 0.80);
}

private void ApplyDirection()
{
    var lang = App.CurrentLanguageCode ?? "en";

    FlowDirection =
        lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

    Title = Loc.Get(
        "Inventory.MaterialTree",
        "Material Directory");

    RbItem.Content = Loc.Get(
        "Inventory.MaterialTreeTexts.Material",
        "Material");
	RbClass.Content = Loc.Get(
		"Inventory.Group",
		"Group");

    RbItem.Content = Loc.Get(
		"Inventory.Material",
		"Material");

    BtnSearch.Content = Loc.Get(
        "Buttons.Search",
        "Search");

    BtnAdd.Content = Loc.Get(
        "Common.New",
        "New");

    BtnRemove.Content = Loc.Get(
        "Buttons.Delete",
        "Delete");

    BtnRefresh.Content = Loc.Get(
        "Buttons.Refresh",
        "Refresh");

    BtnPrint.Content = Loc.Get(
        "Buttons.Print",
        "Print");

    ColCode.Header = Loc.Get(
        "MatCard.Code",
        "Code");

    ColName.Header = Loc.Get(
        "MatCard.Name",
        "Name");

    ColStatus.Header = Loc.Get(
        "MatCard.Status",
        "Status");

    CtxOpen.Header = Loc.Get(
        "Inventory.MaterialTreeTexts.OpenCard",
        "Open Card");

    CtxNew.Header = Loc.Get(
        "Common.New",
        "New");

    CtxEdit.Header = Loc.Get(
        "Buttons.Edit",
        "Edit");

    CtxDelete.Header = Loc.Get(
        "Buttons.Delete",
        "Delete");

    CtxCopyCode.Header = Loc.Get(
        "Inventory.MaterialTreeTexts.CopyCode",
        "Copy Code");

    CtxCopyName.Header = Loc.Get(
        "Inventory.MaterialTreeTexts.CopyName",
        "Copy Name");

    CtxRefresh.Header = Loc.Get(
        "Buttons.Refresh",
        "Refresh");

    CtxPrint.Header = Loc.Get(
        "Buttons.Print",
        "Print");
}

private void OnLanguageChanged(object? sender, EventArgs e)
{
    ApplyDirection();
}

private void Mode_Changed(
    object sender,
    RoutedEventArgs e)
{
    if (!IsLoaded)
        return;

    _modeItem = RbItem.IsChecked == true;

    Reload();
}

private void Reload()
{
    _all.Clear();

    try
    {
        if (_modeItem)
        {
            foreach (var m in MatRepo.LoadAll()
                         .OrderBy(
                             x => x.Code,
                             StringComparer.Ordinal))
            {
                _all.Add(new MatDirListItem
                {
                    ItemKind = MatDirListItem.Kind.Material,
                    Id = m.Id,
                    Code = m.Code,
                    Name = m.Name,
                    NameEn = m.NameEn,
                    Status = m.Status
                });
            }
        }
        else
        {
            LoadGroups();
        }
    }
    catch (Exception ex)
    {
        MessageBox.Show(
            this,
            ex.Message,
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    ApplyFilter();
}

private void LoadGroups()
{
    try
    {
        var gt =
            Type.GetType(
                "ShouTech.App.Services.MatGroupRepo")
            ?? AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(
                    a => a.GetType(
                        "ShouTech.App.Services.MatGroupRepo"))
                .FirstOrDefault(
                    x => x != null);

        if (gt == null)
            return;

        var load = gt.GetMethod(
            "LoadAll",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Static);

        var list =
            load?.Invoke(null, null)
            as System.Collections.IEnumerable;

        if (list == null)
            return;

        foreach (var item in list)
        {
            var type = item.GetType();

            var code =
                type.GetProperty("Code")?
                    .GetValue(item)?
                    .ToString() ?? "";

            if (string.IsNullOrWhiteSpace(code))
                continue;

            _all.Add(new MatDirListItem
            {
                ItemKind = MatDirListItem.Kind.Group,

                Id = Convert.ToInt64(
                    type.GetProperty("Id")?.GetValue(item) ?? 0,
                    CultureInfo.InvariantCulture),

                Code = code,

                Name =
                    type.GetProperty("Name")?
                        .GetValue(item)?
                        .ToString() ?? "",

                NameEn =
                    type.GetProperty("NameEn")?
                        .GetValue(item)?
                        .ToString() ?? "",

                Status =
                    type.GetProperty("Status")?
                        .GetValue(item)?
                        .ToString() ?? "Active"
            });
        }
    }
    catch
    {
        // Groups are optional.
    }
}

private void ApplyFilter()
{
    var q = TxtSearch.Text?.Trim() ?? "";

    _view.Clear();

    IEnumerable<MatDirListItem> source = _all;

    if (!string.IsNullOrEmpty(q))
    {
        source = _all.Where(x =>
            (x.Code?.Contains(
                q,
                StringComparison.OrdinalIgnoreCase) ?? false) ||

            (x.Name?.Contains(
                q,
                StringComparison.OrdinalIgnoreCase) ?? false) ||

            (x.NameEn?.Contains(
                q,
                StringComparison.OrdinalIgnoreCase) ?? false));
    }

    foreach (var item in source)
        _view.Add(item);
}

private void TxtSearch_TextChanged(
    object sender,
    TextChangedEventArgs e)
{
    ApplyFilter();
}

private void Search_Click(
    object sender,
    RoutedEventArgs e)
{
    ApplyFilter();
}

private void Refresh_Click(
    object sender,
    RoutedEventArgs e)
{
    Reload();
}

private void Add_Click(
    object sender,
    RoutedEventArgs e)
{
    if (_modeItem)
    {
        var window = new MatCard
        {
            Owner = this,
            WindowStartupLocation =
                WindowStartupLocation.CenterOwner
        };

        window.Closed += (_, _) => Reload();

        window.Show();
    }
    else
    {
        OpenGroupsWindow();
    }
}

private void Open_Click(
    object sender,
    RoutedEventArgs e)
{
    OpenSelected();
}

private void Lv_DoubleClick(
    object sender,
    MouseButtonEventArgs e)
{
    OpenSelected();
}

private void Lv_KeyDown(
    object sender,
    KeyEventArgs e)
{
    if (e.Key == Key.Enter)
    {
        OpenSelected();
    }
    else if (e.Key == Key.Delete)
    {
        Remove_Click(sender, e);
    }
    else if (e.Key == Key.Insert)
    {
        Add_Click(sender, e);
    }
    else if (e.Key == Key.F5)
    {
        Reload();
    }
}

private void OpenSelected()
{
    if (LvItems.SelectedItem is not MatDirListItem item)
        return;

    if (item.ItemKind == MatDirListItem.Kind.Material)
    {
        var window = new MatCard
        {
            Owner = this,
            WindowStartupLocation =
                WindowStartupLocation.CenterOwner
        };

        window.Closed += (_, _) => Reload();

        window.Show();
    }
    else
    {
        OpenGroupsWindow();
    }
}

private void Remove_Click(
    object sender,
    RoutedEventArgs e)
{
    if (LvItems.SelectedItem is not MatDirListItem item)
        return;

    if (item.ItemKind != MatDirListItem.Kind.Material)
    {
        MessageBox.Show(
            this,
            Loc.Get(
                "Inventory.MaterialTreeTexts.DeleteGroupHint",
                "Delete the group from the Groups window."),
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        OpenGroupsWindow();
        return;
    }

    if (MessageBox.Show(
            this,
            Loc.Get(
                "MatCard.ConfirmDelete",
                "Delete or disable the material?") +
            "\n" +
            item.Code +
            " — " +
            item.DisplayName,
            Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question)
        != MessageBoxResult.Yes)
    {
        return;
    }

    try
    {
        MatRepo.SoftDelete(item.Id);

        Reload();
    }
    catch (Exception ex)
    {
        MessageBox.Show(
            this,
            ex.Message,
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}

private void Print_Click(
    object sender,
    RoutedEventArgs e)
{
    MessageBox.Show(
        this,
        Loc.Get(
            "Inventory.MaterialTreeTexts.PrintHint",
            "Print list") +
        ": " +
        _view.Count.ToString(
            CultureInfo.CurrentCulture),
        Title,
        MessageBoxButton.OK,
        MessageBoxImage.Information);
}

private void Ctx_Opened(
    object sender,
    RoutedEventArgs e)
{
    var hasSelection =
        LvItems.SelectedItem is MatDirListItem;

    if (sender is not ContextMenu contextMenu)
        return;

    foreach (var obj in contextMenu.Items)
    {
        if (obj is not MenuItem menuItem)
            continue;

        if (menuItem.Name is
            "CtxOpen" or
            "CtxEdit" or
            "CtxDelete")
        {
            menuItem.IsEnabled = hasSelection;
        }
    }
}

private void Edit_Click(
    object sender,
    RoutedEventArgs e)
{
    OpenSelected();
}

private void CopyCode_Click(
    object sender,
    RoutedEventArgs e)
{
    if (LvItems.SelectedItem is MatDirListItem item &&
        !string.IsNullOrEmpty(item.Code))
    {
        Clipboard.SetText(item.Code);
    }
}

private void CopyName_Click(
    object sender,
    RoutedEventArgs e)
{
    if (LvItems.SelectedItem is MatDirListItem item)
    {
        Clipboard.SetText(item.DisplayName ?? "");
    }
}

private void OpenGroupsWindow()
{
    try
    {
var type =
Type.GetType(
"ShouTech.App.Views.MatGroupWin")
?? AppDomain.CurrentDomain
.GetAssemblies()
.Select(
a => a.GetType(
"ShouTech.App.Views.MatGroupWin"))
.FirstOrDefault(
x => x != null);


    if (type == null)
    {
        MessageBox.Show(
            this,
            Loc.Get(
                "Inventory.MaterialTreeTexts.NoGroupsWin",
                "The Groups window is not available."),
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        return;
    }

    var window =
        (Window)Activator.CreateInstance(type)!;

    window.Owner = this;

    window.WindowStartupLocation =
        WindowStartupLocation.CenterOwner;

    window.Closed += (_, _) => Reload();

    window.Show();
}
catch (Exception ex)
{
    MessageBox.Show(
        this,
        ex.Message,
        Title,
        MessageBoxButton.OK,
        MessageBoxImage.Error);
}
}

protected override void OnClosed(EventArgs e)
{
    AppHost.Localization.LanguageChanged -= OnLanguageChanged;
    base.OnClosed(e);
}

protected override void ApplyLocalizedText()
{
    ApplyDirection();
}

}
