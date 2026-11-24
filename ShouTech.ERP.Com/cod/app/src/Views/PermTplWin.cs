using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Localization;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Views;

/// <summary>
/// Permission Templates window (PERMISSIONS_STUDY sections 2.2/2.5):
/// the Phenix template window's FUNCTIONAL scope — a template rail,
/// the template's grant set edited per catalog family (the DOCUMENTS
/// family as a document-kind × operation matrix), the assigned-users
/// checklist, and a compact effective-access check strip (the
/// Permission Inspector surfaced) — in ShouTech's own Win11 form.
/// Templates resolve as one flat union in the loader, so the strip's
/// verdict always names the single deciding source. System templates
/// are read-only here; Copy makes an editable company template.
/// All data goes through PermTplSvc (guarded, audited); this window
/// adds no SQL of its own.
/// </summary>
public sealed class PermTplWin : BaseWin
{
    protected override string? ScheduleTintName => "Myrtle";

    private readonly ListBox _lstTemplates = new();
    private readonly TextBlock _lblTemplates = new();
    private readonly TextBlock _lblScope = new();
    private readonly TextBox _txtCode = new();
    private readonly TextBox _txtNameEn = new();
    private readonly TextBox _txtNameAr = new();
    private readonly TextBox _txtDesc = new();
    private readonly CheckBox _chkActive = new();
    private readonly TextBlock _lblCode = new();
    private readonly TextBlock _lblNameEn = new();
    private readonly TextBlock _lblNameAr = new();
    private readonly TextBlock _lblDesc = new();
    private readonly TextBlock _lblGrants = new();
    private readonly ListBox _lstFamilies = new();
    private readonly TextBox _txtSearch = new();
    private readonly ScrollViewer _grantScroll = new();
    private readonly StackPanel _grantList = new();
    private readonly Grid _matrix = new();
    private readonly ScrollViewer _matrixScroll = new();
    private readonly TextBlock _lblUsers = new();
    private readonly ListBox _lstUsers = new();
    private readonly TextBlock _lblCheckAccess = new();
    private readonly ComboBox _cmbUser = new();
    private readonly ComboBox _cmbPermission = new();
    private readonly Button _btnCheck = new();
    private readonly TextBlock _lblVerdict = new();
    private readonly TextBlock _lblStatus = new();
    private readonly Button _btnNew = new();
    private readonly Button _btnCopy = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnClose = new();

    private IReadOnlyList<PermTplSvc.TemplateRow> _templates = Array.Empty<PermTplSvc.TemplateRow>();
    private IReadOnlyList<PermTplSvc.CatalogItem> _catalog = Array.Empty<PermTplSvc.CatalogItem>();
    private IReadOnlyList<PermTplSvc.UserRow> _users = Array.Empty<PermTplSvc.UserRow>();
    private IReadOnlyList<PermAdminSvc.PermissionGroup> _groups = Array.Empty<PermAdminSvc.PermissionGroup>();
    private readonly HashSet<long> _checkedGrants = new();
    private readonly HashSet<string> _assignedUsers = new(StringComparer.OrdinalIgnoreCase);
    private long _editingId;
    private bool _editingSystem;
    private bool _loading;

    public PermTplWin()
    {
        Width = 1180;
        Height = 720;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        ApplyLocalizedText();
        Loaded += OnLoaded;
    }

    // ------------------------------------------------------------------
    // Load / selection
    // ------------------------------------------------------------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.System.PermissionTemplatesManage))
        {
            AppHost.Errors.Report(ErrorCodes.PermissionDenied, null, "PermTplWin.OnLoaded");
            MessageBox.Show(this, L("PermTpl.AccessDenied", "You do not have permission to manage permission templates."),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await ReloadAsync(null);
    }

    private async System.Threading.Tasks.Task ReloadAsync(long? selectId)
    {
        _loading = true;
        try
        {
            _templates = await PermTplSvc.ListTemplatesAsync();
            _catalog = await PermTplSvc.LoadCatalogAsync();
            _users = await PermTplSvc.ListCompanyUsersAsync();
            _groups = await PermAdminSvc.LoadGroupsAsync();
            _lstTemplates.ItemsSource = _templates.Select(t => new ListBoxItem
            {
                Content = $"{t.Code} — {(IsArabic ? t.NameAr : t.NameEn)}",
                Tag = t
            }).ToList();
            _cmbUser.ItemsSource = _users;
            _cmbPermission.ItemsSource = _catalog.Select(c => c.Code).ToList();
            BuildFamilyRail();
            BuildUserList();
            var target = selectId is { } id
                ? _templates.FirstOrDefault(t => t.Id == id)
                : _templates.Count > 0 ? _templates[0] : null;
            if (target is not null)
                await SelectTemplateAsync(target);
            else
                StartNew();
        }
        catch (AppError)
        {
            _lblStatus.Text = L("PermTpl.LoadFailed", "The permission templates could not be loaded.");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowLoadFailed, ex, "PermTplWin.ReloadAsync");
            _lblStatus.Text = L("PermTpl.LoadFailed", "The permission templates could not be loaded.");
        }
        finally { _loading = false; }
    }

    private async System.Threading.Tasks.Task SelectTemplateAsync(PermTplSvc.TemplateRow row)
    {
        _editingId = row.Id;
        _editingSystem = row.IsSystem;
        _txtCode.Text = row.Code;
        _txtNameEn.Text = row.NameEn;
        _txtNameAr.Text = row.NameAr;
        _txtDesc.Text = row.Description ?? string.Empty;
        _chkActive.IsChecked = row.IsActive;
        _lblScope.Text = row.IsSystem
            ? L("PermTpl.SystemTemplate", "System template")
            : L("PermTpl.CompanyTemplate", "Company template");
        try
        {
            var (grants, users) = await PermTplSvc.LoadTemplateDetailAsync(row.Id);
            _checkedGrants.Clear();
            foreach (var g in grants) _checkedGrants.Add(g);
            _assignedUsers.Clear();
            foreach (var u in users) _assignedUsers.Add(u);
        }
        catch (Exception ex)
        {
            if (ex is not AppError)
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PermTplWin.SelectTemplateAsync");
            _lblStatus.Text = L("PermTpl.LoadFailed", "The permission templates could not be loaded.");
            _checkedGrants.Clear();
            _assignedUsers.Clear();
        }
        ApplyReadOnly();
        RefreshFamilyPanel();
        BuildUserList();
    }

    private void StartNew()
    {
        _editingId = 0;
        _editingSystem = false;
        _txtCode.Text = string.Empty;
        _txtNameEn.Text = string.Empty;
        _txtNameAr.Text = string.Empty;
        _txtDesc.Text = string.Empty;
        _chkActive.IsChecked = true;
        _lblScope.Text = L("PermTpl.CompanyTemplate", "Company template");
        _checkedGrants.Clear();
        _assignedUsers.Clear();
        ApplyReadOnly();
        RefreshFamilyPanel();
        BuildUserList();
        _lblStatus.Text = L("PermTpl.NewTemplate", "New template");
    }

    private void ApplyReadOnly()
    {
        var editable = !_editingSystem;
        foreach (var tb in new[] { _txtCode, _txtNameEn, _txtNameAr, _txtDesc })
            tb.IsEnabled = editable;
        _chkActive.IsEnabled = editable;
        _btnSave.IsEnabled = editable;
        _btnDelete.IsEnabled = editable && _editingId > 0;
        _lstUsers.IsEnabled = editable;
    }

    // ------------------------------------------------------------------
    // Grants editor (family rail + checklist / DOC matrix)
    // ------------------------------------------------------------------

    private static bool IsArabic => (App.CurrentLanguageCode ?? "en").StartsWith("ar", StringComparison.OrdinalIgnoreCase);

    private string FamilyDisplay(string groupCode)
    {
        var g = _groups.FirstOrDefault(x => string.Equals(x.Code, groupCode, StringComparison.OrdinalIgnoreCase));
        return g is null ? groupCode : (IsArabic ? g.Name : g.NameEn);
    }

    private void BuildFamilyRail()
    {
        var families = new List<string>();
        foreach (var item in _catalog)
            if (!families.Contains(item.Group, StringComparer.OrdinalIgnoreCase))
                families.Add(item.Group);
        _lstFamilies.ItemsSource = families.Select(f => new ListBoxItem
        {
            Content = FamilyDisplay(f),
            Tag = f
        }).ToList();
        if (_lstFamilies.Items.Count > 0)
            _lstFamilies.SelectedIndex = 0;
    }

    private string CurrentFamily()
        => (_lstFamilies.SelectedItem as ListBoxItem)?.Tag as string ?? string.Empty;

    private void RefreshFamilyPanel()
    {
        var family = CurrentFamily();
        var isDocuments = string.Equals(family, "DOCUMENTS", StringComparison.OrdinalIgnoreCase);
        _matrixScroll.Visibility = isDocuments ? Visibility.Visible : Visibility.Collapsed;
        _grantScroll.Visibility = isDocuments ? Visibility.Collapsed : Visibility.Visible;
        if (isDocuments)
            BuildMatrix();
        else
            BuildGrantList(family);
    }

    private void BuildGrantList(string family)
    {
        _grantList.Children.Clear();
        var filter = _txtSearch.Text.Trim();
        foreach (var item in _catalog.Where(c => string.Equals(c.Group, family, StringComparison.OrdinalIgnoreCase)))
        {
            if (filter.Length > 0
                && !item.Code.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !item.NameEn.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var name = IsArabic ? item.Name : item.NameEn;
            var check = new CheckBox
            {
                Content = item.Sensitive ? $"{name} — {item.Code} *" : $"{name} — {item.Code}",
                Tag = item.Id,
                IsChecked = _checkedGrants.Contains(item.Id),
                IsEnabled = !_editingSystem,
                Margin = new Thickness(2, 2, 2, 2)
            };
            check.Checked += (_, _) => _checkedGrants.Add((long)check.Tag);
            check.Unchecked += (_, _) => _checkedGrants.Remove((long)check.Tag);
            _grantList.Children.Add(check);
        }
    }

    private void BuildMatrix()
    {
        _matrix.Children.Clear();
        _matrix.ColumnDefinitions.Clear();
        _matrix.RowDefinitions.Clear();
        var ops = PermCat.Documents.Operations;
        var kinds = PermCat.Documents.Kinds;
        _matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        foreach (var _ in ops)
            _matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _matrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var _ in kinds)
            _matrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var c = 0; c < ops.Length; c++)
        {
            var header = new TextBlock
            {
                Text = ops[c],
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2)
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, c + 1);
            _matrix.Children.Add(header);
        }

        var byCode = _catalog.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        for (var r = 0; r < kinds.Length; r++)
        {
            var kindLabel = new TextBlock
            {
                Text = kinds[r],
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2)
            };
            Grid.SetRow(kindLabel, r + 1);
            Grid.SetColumn(kindLabel, 0);
            _matrix.Children.Add(kindLabel);
            for (var c = 0; c < ops.Length; c++)
            {
                var code = PermCat.Documents.Key(kinds[r], ops[c]);
                if (!byCode.TryGetValue(code, out var item))
                    continue;
                var cell = new CheckBox
                {
                    Tag = item.Id,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2),
                    IsChecked = _checkedGrants.Contains(item.Id),
                    IsEnabled = !_editingSystem
                };
                cell.Checked += (_, _) => _checkedGrants.Add((long)cell.Tag);
                cell.Unchecked += (_, _) => _checkedGrants.Remove((long)cell.Tag);
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c + 1);
                _matrix.Children.Add(cell);
            }
        }
    }

    private void BuildUserList()
    {
        _lstUsers.ItemsSource = _users.Select(u => new CheckBox
        {
            Content = $"{u.Username} — {u.FullName}",
            Tag = u.Id,
            IsChecked = _assignedUsers.Contains(u.Id),
            Margin = new Thickness(2)
        }).ToList();
        foreach (CheckBox check in _lstUsers.Items)
        {
            check.Checked += (_, _) => _assignedUsers.Add((string)check.Tag);
            check.Unchecked += (_, _) => _assignedUsers.Remove((string)check.Tag);
        }
    }

    // ------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var code = _txtCode.Text.Trim();
        if (code.Length == 0)
        {
            _lblStatus.Text = L("PermTpl.CodeRequired", "Enter a template code.");
            return;
        }
        if (_txtNameEn.Text.Trim().Length == 0 && _txtNameAr.Text.Trim().Length == 0)
        {
            _lblStatus.Text = L("PermTpl.NameRequired", "Enter a template name.");
            return;
        }
        try
        {
            _btnSave.IsEnabled = false;
            var id = await PermTplSvc.SaveTemplateAsync(_editingId, code,
                _txtNameEn.Text.Trim(), _txtNameAr.Text.Trim(), _txtDesc.Text.Trim(),
                _chkActive.IsChecked == true, _checkedGrants.ToList(), _assignedUsers.ToList());
            _lblStatus.Text = L("PermTpl.Saved", "Template saved.");
            await ReloadAsync(id);
        }
        catch (AppError ex)
        {
            _lblStatus.Text = $"{L("PermTpl.SaveFailed", "The permission template could not be saved.")} [{ex.Code}]";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PermTplWin.Save_Click");
            _lblStatus.Text = L("PermTpl.SaveFailed", "The permission template could not be saved.");
        }
        finally { _btnSave.IsEnabled = !_editingSystem; }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_editingId == 0)
        {
            _lblStatus.Text = L("PermTpl.SelectTemplate", "Select a template.");
            return;
        }
        _editingId = 0;
        _editingSystem = false;
        _txtCode.Text = _txtCode.Text.Trim() + "_COPY";
        _assignedUsers.Clear();
        ApplyReadOnly();
        RefreshFamilyPanel();
        BuildUserList();
        _lblStatus.Text = L("PermTpl.NewTemplate", "New template");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editingId == 0 || _editingSystem)
            return;
        if (MessageBox.Show(this, L("PermTpl.ConfirmDelete", "Delete this template? Its user assignments are removed and it stops applying immediately."),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            await PermTplSvc.DeleteTemplateAsync(_editingId);
            _lblStatus.Text = L("PermTpl.Deleted", "Template deleted.");
            await ReloadAsync(null);
        }
        catch (AppError ex)
        {
            _lblStatus.Text = $"{L("PermTpl.SaveFailed", "The permission template could not be saved.")} [{ex.Code}]";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PermTplWin.Delete_Click");
            _lblStatus.Text = L("PermTpl.SaveFailed", "The permission template could not be saved.");
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_cmbUser.SelectedItem is not PermTplSvc.UserRow user)
        {
            _lblVerdict.Text = string.Empty;
            return;
        }
        var key = (_cmbPermission.Text ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            _lblVerdict.Text = string.Empty;
            return;
        }
        try
        {
            var verdict = await AppHost.Permissions.ExplainAsync(user.Id, key);
            var source = verdict.Source switch
            {
                PermSourceKind.AdministratorRole => L("PermTpl.SourceAdmin", "Granted by the administrator role"),
                PermSourceKind.Role => Tf("PermTpl.SourceRole", verdict.SourceName),
                PermSourceKind.Template => Tf("PermTpl.SourceTemplate", verdict.SourceName),
                PermSourceKind.UserAllow => L("PermTpl.SourceUserAllow", "Granted by a per-user allow"),
                PermSourceKind.UserDeny => L("PermTpl.SourceUserDeny", "Blocked by an explicit per-user deny"),
                _ => L("PermTpl.SourceNone", "Not granted anywhere")
            };
            _lblVerdict.Text = $"{(verdict.Allowed ? L("PermTpl.Granted", "Granted") : L("PermTpl.Denied", "Denied"))} — {source}";
            _lblVerdict.Foreground = verdict.Allowed ? Brushes.DarkGreen : Brushes.Firebrick;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PermTplWin.Check_Click");
            _lblVerdict.Text = string.Empty;
        }
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var main = new Grid { Margin = new Thickness(10, 10, 10, 4) };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(main, 0);
        root.Children.Add(main);

        // Template rail.
        var rail = new Grid();
        rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _lblTemplates.FontWeight = FontWeights.SemiBold;
        Grid.SetRow(_lblTemplates, 0);
        rail.Children.Add(_lblTemplates);
        _lstTemplates.SelectionChanged += async (_, _) =>
        {
            if (_loading) return;
            if ((_lstTemplates.SelectedItem as ListBoxItem)?.Tag is PermTplSvc.TemplateRow row)
                await SelectTemplateAsync(row);
        };
        Grid.SetRow(_lstTemplates, 1);
        rail.Children.Add(_lstTemplates);
        Grid.SetColumn(rail, 0);
        main.Children.Add(rail);

        var splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetColumn(splitter, 1);
        main.Children.Add(splitter);

        // Detail.
        var detail = new Grid { Margin = new Thickness(6, 0, 0, 0) };
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(detail, 2);
        main.Children.Add(detail);

        var meta = new Grid();
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++)
            meta.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddMetaField(meta, _lblCode, _txtCode, 0, 0);
        AddMetaField(meta, _lblNameEn, _txtNameEn, 0, 2);
        AddMetaField(meta, _lblNameAr, _txtNameAr, 1, 0);
        AddMetaField(meta, _lblDesc, _txtDesc, 1, 2);
        _chkActive.Margin = new Thickness(2, 6, 2, 2);
        Grid.SetRow(_chkActive, 2);
        Grid.SetColumn(_chkActive, 1);
        meta.Children.Add(_chkActive);
        _lblScope.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        Grid.SetRow(_lblScope, 2);
        Grid.SetColumn(_lblScope, 3);
        meta.Children.Add(_lblScope);
        Grid.SetRow(meta, 0);
        detail.Children.Add(meta);

        var editor = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        Grid.SetRow(editor, 1);
        detail.Children.Add(editor);

        // Grants: family rail + panel.
        var grantsPanel = new Grid();
        grantsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grantsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grantsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _lblGrants.FontWeight = FontWeights.SemiBold;
        Grid.SetRow(_lblGrants, 0);
        grantsPanel.Children.Add(_lblGrants);
        var grantsSplit = new Grid();
        grantsSplit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grantsSplit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _lstFamilies.SelectionChanged += (_, _) => { if (!_loading) RefreshFamilyPanel(); };
        Grid.SetColumn(_lstFamilies, 0);
        grantsSplit.Children.Add(_lstFamilies);
        var panelHost = new Grid { Margin = new Thickness(6, 0, 0, 0) };
        _grantScroll.Content = _grantList;
        _grantScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        panelHost.Children.Add(_grantScroll);
        _matrixScroll.Content = _matrix;
        _matrixScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _matrixScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _matrixScroll.Visibility = Visibility.Collapsed;
        panelHost.Children.Add(_matrixScroll);
        Grid.SetColumn(panelHost, 1);
        grantsSplit.Children.Add(panelHost);
        Grid.SetRow(grantsSplit, 2);
        grantsPanel.Children.Add(grantsSplit);
        _txtSearch.Margin = new Thickness(0, 4, 0, 4);
        _txtSearch.TextChanged += (_, _) => { if (!_loading) RefreshFamilyPanel(); };
        Grid.SetRow(_txtSearch, 1);
        grantsPanel.Children.Add(_txtSearch);
        Grid.SetColumn(grantsPanel, 0);
        editor.Children.Add(grantsPanel);

        var editorSplitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetColumn(editorSplitter, 1);
        editor.Children.Add(editorSplitter);

        var usersPanel = new Grid();
        usersPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        usersPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _lblUsers.FontWeight = FontWeights.SemiBold;
        Grid.SetRow(_lblUsers, 0);
        usersPanel.Children.Add(_lblUsers);
        Grid.SetRow(_lstUsers, 1);
        usersPanel.Children.Add(_lstUsers);
        Grid.SetColumn(usersPanel, 2);
        editor.Children.Add(usersPanel);

        // Inspector strip.
        var strip = new Grid { Margin = new Thickness(10, 4, 10, 4) };
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _lblCheckAccess.FontWeight = FontWeights.SemiBold;
        _lblCheckAccess.VerticalAlignment = VerticalAlignment.Center;
        _lblCheckAccess.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(_lblCheckAccess, 0);
        strip.Children.Add(_lblCheckAccess);
        _cmbUser.DisplayMemberPath = "Username";
        Grid.SetColumn(_cmbUser, 1);
        strip.Children.Add(_cmbUser);
        _cmbPermission.IsEditable = true;
        _cmbPermission.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_cmbPermission, 2);
        strip.Children.Add(_cmbPermission);
        _btnCheck.Margin = new Thickness(8, 0, 0, 0);
        _btnCheck.Padding = new Thickness(12, 3, 12, 3);
        _btnCheck.Click += Check_Click;
        Grid.SetColumn(_btnCheck, 3);
        strip.Children.Add(_btnCheck);
        _lblVerdict.VerticalAlignment = VerticalAlignment.Center;
        _lblVerdict.Margin = new Thickness(10, 0, 0, 0);
        _lblVerdict.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(_lblVerdict, 4);
        strip.Children.Add(_lblVerdict);
        var stripBorder = new Border
        {
            Child = strip,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        stripBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetRow(stripBorder, 1);
        root.Children.Add(stripBorder);

        // Bottom action bar (owner rule: actions at the bottom).
        var bar = new Grid { Margin = new Thickness(10, 6, 10, 10) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _btnNew.Click += (_, _) => StartNew();
        _btnCopy.Click += Copy_Click;
        _btnDelete.Click += Delete_Click;
        _btnSave.Click += Save_Click;
        _btnClose.Click += (_, _) => Close();
        var left = new[] { _btnNew, _btnCopy, _btnDelete };
        for (var i = 0; i < left.Length; i++)
        {
            left[i].Margin = new Thickness(0, 0, 6, 0);
            left[i].Padding = new Thickness(12, 4, 12, 4);
            Grid.SetColumn(left[i], i);
            bar.Children.Add(left[i]);
        }
        _lblStatus.VerticalAlignment = VerticalAlignment.Center;
        _lblStatus.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        Grid.SetColumn(_lblStatus, 3);
        bar.Children.Add(_lblStatus);
        _btnSave.Padding = new Thickness(14, 4, 14, 4);
        Grid.SetColumn(_btnSave, 4);
        bar.Children.Add(_btnSave);
        _btnClose.Margin = new Thickness(6, 0, 0, 0);
        _btnClose.Padding = new Thickness(12, 4, 12, 4);
        Grid.SetColumn(_btnClose, 5);
        bar.Children.Add(_btnClose);
        var barBorder = new Border
        {
            Child = bar,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetRow(barBorder, 2);
        root.Children.Add(barBorder);

        Content = root;
    }

    private static void AddMetaField(Grid grid, TextBlock label, TextBox box, int row, int col)
    {
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(2);
        Grid.SetRow(label, row);
        Grid.SetColumn(label, col);
        grid.Children.Add(label);
        box.Margin = new Thickness(2);
        Grid.SetRow(box, row);
        Grid.SetColumn(box, col + 1);
        grid.Children.Add(box);
    }

    protected override void ApplyLocalizedText()
    {
        Title = L("PermTpl.Title", "Permission Templates");
        _lblTemplates.Text = L("PermTpl.Templates", "Templates");
        _lblCode.Text = L("PermTpl.Code", "Code");
        _lblNameEn.Text = L("PermTpl.NameEn", "Name (English)");
        _lblNameAr.Text = L("PermTpl.NameAr", "Name (Arabic)");
        _lblDesc.Text = L("PermTpl.Description", "Description");
        _chkActive.Content = L("PermTpl.Active", "Active");
        _lblGrants.Text = L("PermTpl.Grants", "Permissions");
        _lblUsers.Text = L("PermTpl.AssignedUsers", "Assigned users");
        _lblCheckAccess.Text = L("PermTpl.CheckAccess", "Effective access check");
        _txtSearch.ToolTip = L("PermTpl.Search", "Search");
        _btnCheck.Content = L("PermTpl.Check", "Check");
        _btnNew.Content = L("PermTpl.New", "New");
        _btnCopy.Content = L("PermTpl.Copy", "Copy");
        _btnDelete.Content = L("PermTpl.Delete", "Delete");
        _btnSave.Content = L("PermTpl.Save", "Save");
        _btnClose.Content = L("PermTpl.Close", "Close");
    }

    private static string L(string key, string fallback)
    {
        try { return Loc.Get(key, fallback); }
        catch { return fallback; }
    }
}
