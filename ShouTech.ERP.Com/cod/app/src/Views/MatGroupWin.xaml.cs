#nullable enable

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using ShouTech.App.Localization;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

public partial class MatGroupWin : BaseWin
{
    private readonly ObservableCollection<GroupMaterialRow> _rows = new();
    private ICollectionView? _view;
    private bool _loading;
    private bool _saving;

    public MatGroupWin()
    {
        InitializeComponent();
        VisuId.ApplyWindow(this);
        GridMaterials.ItemsSource = _rows;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        Loaded += OnWindowLoaded;
    }

    protected override void ApplyLocalizedText()
    {
        Title = L("Title", "Material Groups");
        TxtTitle.Text = L("Title", "Material Groups");
        TxtSubtitle.Text = L("Subtitle", "Assign existing materials to reusable groups. A material may belong to more than one group.");
        LblGroup.Text = L("Group", "Material group");
        LblSearch.Text = L("Search", "Search");
        BtnSelectVisible.Content = L("SelectVisible", "Select visible");
        BtnClearVisible.Content = L("ClearVisible", "Clear visible");
        BtnManageGroups.Content = L("ManageGroups", "Manage group cards");
        BtnRefresh.Content = L("Refresh", "Refresh");
        BtnSave.Content = L("Save", "Save membership");
        BtnClose.Content = L("Close", "Close");
        ColAssigned.Header = L("Assigned", "Assigned");
        ColCode.Header = L("Code", "Code");
        ColName.Header = L("Material", "Material");
        ColNameEn.Header = L("English name", "English name");
        ColStatus.Header = L("Status", "Status");
        UpdateSelectionCount();
    }

    private static string L(string key, string fallback)
        => Loc.Get("MatGroupWin." + key, fallback);

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;
        ReloadGroups();
    }

    private void ReloadGroups(long? preferredGroupId = null)
    {
        _loading = true;
        try
        {
            var groups = MatRepo.LoadGroups()
                .Where(group => !group.IsDeleted)
                .Select(group => new GroupOption
                {
                    Id = group.Id,
                    Code = group.Code,
                    DisplayName = ResolveGroupName(group)
                })
                .OrderBy(group => group.Code, StringComparer.Ordinal)
                .ToList();

            CmbGroup.ItemsSource = groups;
            var selected = groups.FirstOrDefault(group => group.Id == preferredGroupId)
                ?? groups.FirstOrDefault();
            CmbGroup.SelectedItem = selected;

            if (selected is null)
            {
                _rows.Clear();
                TxtStatus.Text = L("NoGroups", "No active material groups exist. Create a group card first.");
                UpdateSelectionCount();
            }
        }
        catch (Exception ex)
        {
            ReportError("MGR-001", "Loading material groups failed.", ex);
            ShowError(L("LoadFailed", "Material groups could not be loaded."));
        }
        finally
        {
            _loading = false;
        }

        if (CmbGroup.SelectedItem is GroupOption option)
            LoadGroupMaterials(option.Id);
    }

    private static string ResolveGroupName(MatRepo.MatGroupRec group)
    {
        var language = App.CurrentLanguageCode ?? "en";
        var name = language.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
            ? group.Name
            : group.NameEn;
        if (string.IsNullOrWhiteSpace(name))
            name = group.Name;
        return string.IsNullOrWhiteSpace(name) ? group.Code : name;
    }

    private void LoadGroupMaterials(long groupId)
    {
        if (_loading)
            return;

        _loading = true;
        try
        {
            var assigned = MatGroupRepo.LoadMemberIds(groupId);
            var materials = MatRepo.LoadAll();
            _rows.Clear();

            var arabic = (App.CurrentLanguageCode ?? "en")
                .StartsWith("ar", StringComparison.OrdinalIgnoreCase);

            foreach (var material in materials.OrderBy(item => item.Code, StringComparer.Ordinal))
            {
                var displayName = arabic
                    ? material.Name
                    : material.NameEn;
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = material.Name;

                _rows.Add(new GroupMaterialRow
                {
                    Id = material.Id,
                    Code = material.Code,
                    DisplayName = displayName,
                    NameEn = material.NameEn,
                    Status = material.Status,
                    IsAssigned = assigned.Contains(material.Id)
                });
            }

            _view?.Refresh();
            TxtStatus.Text = L("Loaded", "Material list loaded. Changes are not saved until you select Save membership.");
            UpdateSelectionCount();
        }
        catch (Exception ex)
        {
            ReportError("MGR-002", "Loading group membership failed.", ex);
            ShowError(L("LoadFailed", "Material group membership could not be loaded."));
        }
        finally
        {
            _loading = false;
        }
    }

    private bool FilterRow(object value)
    {
        if (value is not GroupMaterialRow row)
            return false;

        var query = TxtSearch?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return row.Code.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.NameEn.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void CmbGroup_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && CmbGroup.SelectedItem is GroupOption group)
            LoadGroupMaterials(group.Id);
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateSelectionCount();
    }

    private void BtnSelectVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.Where(FilterRow))
            row.IsAssigned = true;
        UpdateSelectionCount();
    }

    private void BtnClearVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.Where(FilterRow))
            row.IsAssigned = false;
        UpdateSelectionCount();
    }

    private void BtnManageGroups_Click(object sender, RoutedEventArgs e)
    {
        var window = new CatWin
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.ShowDialog();
        ReloadGroups((CmbGroup.SelectedItem as GroupOption)?.Id);
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        TxtSearch.Clear();
        ReloadGroups((CmbGroup.SelectedItem as GroupOption)?.Id);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || CmbGroup.SelectedItem is not GroupOption group)
        {
            ShowWarn(L("SelectGroup", "Select a material group first."));
            return;
        }

        GridMaterials.CommitEdit(DataGridEditingUnit.Cell, true);
        GridMaterials.CommitEdit(DataGridEditingUnit.Row, true);

        _saving = true;
        BtnSave.IsEnabled = false;
        try
        {
            MatGroupRepo.SaveMembers(group.Id, _rows.Where(row => row.IsAssigned).Select(row => row.Id));
            TxtStatus.Text = L("Saved", "Material group membership saved.");
            UpdateSelectionCount();
        }
        catch (Exception ex)
        {
            ReportError("MGR-003", "Saving group membership failed.", ex);
            ShowError(L("SaveFailed", "Material group membership could not be saved."));
        }
        finally
        {
            _saving = false;
            BtnSave.IsEnabled = true;
        }
    }

    private void UpdateSelectionCount()
    {
        if (TxtSelectionCount is null)
            return;

        var total = _rows.Count;
        var assigned = _rows.Count(row => row.IsAssigned);
        TxtSelectionCount.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            L("Count", "{0} assigned / {1} materials"),
            assigned,
            total);
    }

    private void ReportError(string code, string message, Exception exception)
        => AppHost.Logging?.LogError("MaterialGroups", $"[{code}] {message}", exception);

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class GroupOption
    {
        public long Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
    }

    private sealed class GroupMaterialRow : INotifyPropertyChanged
    {
        private bool _isAssigned;

        public long Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string NameEn { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;

        public bool IsAssigned
        {
            get => _isAssigned;
            set
            {
                if (_isAssigned == value)
                    return;
                _isAssigned = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAssigned)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
