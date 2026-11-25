using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using ShouTech.App.Localization;
using ShouTech.App.Services;

using ShouTech.Domain;
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
        if (ItemKind == Kind.Material)
            return Name;

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
/// One node of the material directory tree (owner grid spec
/// item 6): a group (category) node carries child groups and
/// materials; a material node is a leaf. IsExpanded binds two-way
/// to the TreeViewItem so the template can swap the folder glyph
/// (closed / open) — modern Segoe MDL2 glyphs, per the owner's
/// modern-visuals rule.
/// </summary>
public sealed class MatDirNode : System.ComponentModel.INotifyPropertyChanged
{
    public MatDirNode(MatDirListItem item) => Item = item;

    public MatDirListItem Item { get; }

    public ObservableCollection<MatDirNode> Children { get; } = new();

    public bool IsGroup => Item.ItemKind == MatDirListItem.Kind.Group;

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Material and group directory.
/// </summary>
public partial class MatDirWin : BaseWin, IInfoContextProvider
{
    protected override string? ScheduleTintName => "Sand";

private readonly ObservableCollection<MatDirListItem> _all = new();
private readonly ObservableCollection<MatDirListItem> _view = new();
private readonly ObservableCollection<MatDirNode> _roots = new();

// Raw load state for tree building: groups with their parent
// linkage, and the (group, material) membership pairs.
private List<MatGroupRepo.MatGroupRec> _groups = new();
private List<MatGroupRepo.MatGroupMember> _membership = new();

// ------------------------------------------------------------------
// Picker mode (owner grid spec 2026-11-24 15:07 item 3): document
// grids whose typed material text matches nothing — or matches
// several materials — open THIS directory as the search window,
// pre-filtered with the typed text; double-click/Enter returns the
// chosen material. The directory is the search surface (the
// Al-Ameen pattern); no parallel lookup window exists.
// ------------------------------------------------------------------
private bool _pickerMode;

public MatDirListItem? PickedItem { get; private set; }

public static MatDirListItem? ShowPicker(Window owner, string? initialSearch)
{
    var win = new MatDirWin { Owner = owner };
    win.BeginPick(initialSearch);
    win.ShowDialog();
    return win.PickedItem;
}

private void BeginPick(string? initialSearch)
{
    _pickerMode = true;
    BtnAdd.Visibility = Visibility.Collapsed;
    BtnRemove.Visibility = Visibility.Collapsed;
    Reload();
    if (!string.IsNullOrWhiteSpace(initialSearch))
        TxtSearch.Text = initialSearch;
    TxtSearch.Focus();
    TxtSearch.SelectAll();
}

public MatDirWin()
{
    InitializeComponent();
    VisuId.ApplyWindow(this);

    AppHost.Localization.LanguageChanged += OnLanguageChanged;

    ApplyDirection();

    TvItems.ItemsSource = _roots;
    TvItems.SelectedItemChanged += (_, _) => PublishInfoSelection();

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
    // Central direction mechanism (also applied by BaseWin); no local
    // language-prefix checks.
    LangDirSv.ApplyToWindow(this, App.CurrentLanguageCode);

    Title = Loc.Get(
        "Inventory.MaterialTree",
        "Material Directory");

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

private MatDirListItem? SelectedItem
    => (TvItems.SelectedItem as MatDirNode)?.Item;

// Global Information Window provider (INFO_WINDOW_STUDY): a material
// node publishes its material; a group node publishes nothing (groups
// have no property sheet — the study's entity set has no group kind).
public InfoRef? CurrentInfoRef
    => SelectedItem is { ItemKind: MatDirListItem.Kind.Material } item
        ? new InfoRef(InfoKind.Material, Id: item.Id)
        : null;

public InfoContext? CurrentInfoContext => null;

private void Reload()
{
    _all.Clear();
    _groups = new();
    _membership = new();

    try
    {
        // One unified load for the tree: materials, groups and
        // their membership. (The old two-mode load called a
        // MatGroupRepo.LoadAll that did not exist — through
        // reflection — so Groups mode silently showed nothing.
        // The repo method exists now and is called directly.)
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
                Status = m.Status
            });
        }

        _groups = MatGroupRepo.LoadAll();
        _membership = MatGroupRepo.LoadMembership();

        foreach (var g in _groups)
        {
            _all.Add(new MatDirListItem
            {
                ItemKind = MatDirListItem.Kind.Group,
                Id = g.Id,
                Code = g.Code,
                Name = g.Name,
                NameEn = g.NameEn,
                Status = g.Status
            });
        }
    }
    catch (Exception ex)
    {
        AppHost.Logging?.LogError("MatDirWin", "Reload failed", ex);
        MessageBox.Show(
            this,
            ex.Message,
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    ApplyFilter();
}

// ------------------------------------------------------------------
// Tree building (owner grid spec item 6). Groups nest by their
// parent_code; materials attach through material_group_items
// membership (a material in several groups appears under each);
// a material with no membership sits at the root; a group may be
// empty. With a search query the tree prunes to matches plus
// their ancestor groups, expanded; a group that itself matches
// keeps all of its children.
// ------------------------------------------------------------------

private static bool Matches(MatDirListItem item, string q)
    => (item.Code?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
       || (item.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
       || (item.NameEn?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

private void ApplyFilter()
{
    var q = TxtSearch.Text?.Trim() ?? "";

    _roots.Clear();
    _view.Clear();

    var materialsById = _all
        .Where(x => x.ItemKind == MatDirListItem.Kind.Material)
        .ToDictionary(x => x.Id);

    // Group nodes keyed by code (parent linkage is by code).
    var groupNodes = new Dictionary<string, MatDirNode>(StringComparer.OrdinalIgnoreCase);
    foreach (var g in _groups.OrderBy(x => x.Code, StringComparer.Ordinal))
    {
        var item = _all.First(x => x.ItemKind == MatDirListItem.Kind.Group && x.Id == g.Id);
        groupNodes[g.Code] = new MatDirNode(item);
    }

    // Nest sub-groups under their parents; orphans go to the root.
    var nested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var g in _groups)
    {
        if (!string.IsNullOrWhiteSpace(g.ParentCode)
            && groupNodes.TryGetValue(g.ParentCode!, out var parent)
            && groupNodes.TryGetValue(g.Code, out var child)
            && !ReferenceEquals(parent, child))
        {
            parent.Children.Add(child);
            nested.Add(g.Code);
        }
    }

    var memberIds = new HashSet<long>();
    foreach (var pair in _membership)
    {
        memberIds.Add(pair.MaterialId);
        var group = _groups.FirstOrDefault(x => x.Id == pair.GroupId);
        if (group is null || !groupNodes.TryGetValue(group.Code, out var groupNode))
            continue;
        if (materialsById.TryGetValue(pair.MaterialId, out var material))
            groupNode.Children.Add(new MatDirNode(material));
    }

    var topLevel = new List<MatDirNode>();
    foreach (var g in _groups.OrderBy(x => x.Code, StringComparer.Ordinal))
    {
        if (!nested.Contains(g.Code))
            topLevel.Add(groupNodes[g.Code]);
    }
    foreach (var m in _all.Where(x => x.ItemKind == MatDirListItem.Kind.Material && !memberIds.Contains(x.Id))
                 .OrderBy(x => x.Code, StringComparer.Ordinal))
    {
        topLevel.Add(new MatDirNode(m));
    }

    foreach (var node in topLevel)
    {
        var kept = string.IsNullOrEmpty(q) ? node : Prune(node, q);
        if (kept is not null)
        {
            _roots.Add(kept);
            FlattenInto(kept, _view);
        }
    }
}

/// <summary>Returns the node to keep under a search filter (the
/// node itself, with pruned children), or null to drop it.</summary>
private static MatDirNode? Prune(MatDirNode node, string q)
{
    if (!node.IsGroup)
        return Matches(node.Item, q) ? node : null;

    if (Matches(node.Item, q))
    {
        node.IsExpanded = true;
        return node; // a matching group keeps all of its children
    }

    var keptChildren = new List<MatDirNode>();
    foreach (var child in node.Children)
    {
        var kept = Prune(child, q);
        if (kept is not null)
            keptChildren.Add(kept);
    }
    if (keptChildren.Count == 0)
        return null;

    node.Children.Clear();
    foreach (var child in keptChildren)
        node.Children.Add(child);
    node.IsExpanded = true;
    return node;
}

private static void FlattenInto(MatDirNode node, ObservableCollection<MatDirListItem> target)
{
    target.Add(node.Item);
    foreach (var child in node.Children)
        FlattenInto(child, target);
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
    // With a group selected, New manages that group (the old
    // Groups-mode add path); otherwise it creates a material.
    if (SelectedItem is { ItemKind: MatDirListItem.Kind.Group } group)
    {
        OpenGroupsWindow(group.Id);
        return;
    }

    var window = new MatCard
    {
        Owner = this,
        WindowStartupLocation =
            WindowStartupLocation.CenterOwner
    };

    window.Closed += (_, _) => Reload();

    window.Show();
}

private void Open_Click(
    object sender,
    RoutedEventArgs e)
{
    OpenSelected();
}

private void Tv_DoubleClick(
    object sender,
    MouseButtonEventArgs e)
{
    OpenSelected();
}

private void Tv_KeyDown(
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
    if (SelectedItem is not { } item)
        return;

    // Picker mode (document-grid material search): double-click /
    // Enter accepts the material instead of opening its card.
    if (_pickerMode && item.ItemKind == MatDirListItem.Kind.Material)
    {
        PickedItem = item;
        DialogResult = true;
        Close();
        return;
    }

    if (item.ItemKind == MatDirListItem.Kind.Material)
    {
        var window = new MatCard
        {
            Owner = this,
            WindowStartupLocation =
                WindowStartupLocation.CenterOwner
        };

        window.Closed += (_, _) => Reload();

        // Open at the SELECTED material (was: selection discarded, the
        // card always showed the first material in its cache).
        window.OpenMaterial(item.Id);
        window.Show();
    }
    else
    {
        OpenGroupsWindow(item.Id);
    }
}

private void Remove_Click(
    object sender,
    RoutedEventArgs e)
{
    if (SelectedItem is not { } item)
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
        AppHost.Logging?.LogError("MatDirWin", "Remove material failed", ex);
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
  try
  {
    // Real list print (was a fake handler that only showed the row count
    // in a message box). Same FlowDocument pattern as CoaWin's print.
    var dialog = new PrintDialog();
    if (dialog.ShowDialog() != true)
        return;

    var document = new FlowDocument
    {
        PagePadding = new Thickness(40),
        FlowDirection = FlowDirection,
        FontFamily = VisuId.ResolveFontFamily("ApplicationFontFamily", "Segoe UI"),
        FontSize = VisuId.ResolveFontSize(11)
    };
    document.Blocks.Add(new Paragraph(new Run(Title))
    {
        FontSize = VisuId.ResolveFontSize(18),
        FontWeight = FontWeights.Bold,
        TextAlignment = TextAlignment.Center
    });
    document.Blocks.Add(new Paragraph(new Run(
        DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
    {
        TextAlignment = TextAlignment.Right
    });

    foreach (var item in _view)
    {
        document.Blocks.Add(new Paragraph(new Run(
            $"{item.Code}    {item.DisplayName}    {item.StatusLabel}"))
        {
            Margin = new Thickness(0, 1, 0, 1),
            TextAlignment = FlowDirection == FlowDirection.RightToLeft
                ? TextAlignment.Right
                : TextAlignment.Left
        });
    }

    dialog.PrintDocument(
        ((IDocumentPaginatorSource)document).DocumentPaginator,
        Title);
  }
  catch (Exception ex)
  {
    AppHost.Logging?.LogError("MatDirWin", "Print failed", ex);
    MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
  }
}

private void Ctx_Opened(
    object sender,
    RoutedEventArgs e)
{
    var hasSelection =
        SelectedItem is not null;

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
    if (SelectedItem is { } item &&
        !string.IsNullOrEmpty(item.Code))
    {
        Clipboard.SetText(item.Code);
    }
}

private void CopyName_Click(
    object sender,
    RoutedEventArgs e)
{
    if (SelectedItem is { } item)
    {
        Clipboard.SetText(item.DisplayName ?? "");
    }
}

private void OpenGroupsWindow(long? groupId = null)
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

    // Focus the SELECTED group when opened from a selection (was: the
    // groups window always showed its first group).
    if (groupId.HasValue && window is MatGroupWin groupWin)
        groupWin.SelectGroup(groupId.Value);

    window.Owner = this;

    window.WindowStartupLocation =
        WindowStartupLocation.CenterOwner;

    window.Closed += (_, _) => Reload();

    window.Show();
}
catch (Exception ex)
{
    AppHost.Logging?.LogError("MatDirWin", "Open material groups failed", ex);
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
