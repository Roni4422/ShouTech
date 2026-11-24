using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Services;

namespace ShouTech.App.ViewModels;

public sealed class PrmWinVM : INotifyPropertyChanged
{
    private readonly string _userId;
    private bool _isBusy;
    private string _status = string.Empty;

    public ObservableCollection<PermissionTabVM> Tabs { get; } = new();
    public string UserId => _userId;
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; OnChanged(); } }
    public string Status { get => _status; private set { _status = value; OnChanged(); } }

    public PrmWinVM(string userId) => _userId = userId?.Trim() ?? throw new ArgumentNullException(nameof(userId));

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var groups = await PermAdminSvc.LoadGroupsAsync(ct).ConfigureAwait(true);
            var permissions = await PermAdminSvc.LoadForUserAsync(_userId, ct).ConfigureAwait(true);
            Tabs.Clear();
            // Group by the permission's STORED group_code (migration
            // 125 made group_code authoritative). The old code-prefix
            // map silently hid every row whose prefix it did not know
            // (HR, QUALITY, AST, PRJ, DOC, ...). Two legacy stored
            // codes fold into their catalog groups; any stored group
            // missing from the catalog still gets its own tab, so the
            // window always shows 100% of the loaded catalog.
            var byGroup = new Dictionary<string, List<PermAdminSvc.PermissionItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in permissions)
            {
                var key = StoredGroupAlias(p.Group);
                if (!byGroup.TryGetValue(key, out var list)) byGroup[key] = list = new List<PermAdminSvc.PermissionItem>();
                list.Add(p);
            }
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                if (!byGroup.TryGetValue(group.Code, out var items)) continue;
                var tab = new PermissionTabVM(group.Code, AppLanguage(group.Name, group.NameEn));
                foreach (var p in items)
                    tab.Items.Add(new PermissionItemVM(p.Id, p.Code, p.Feature, p.Operation, AppLanguage(p.Name, p.NameEn), p.EffectiveAllowed, p.Sensitive));
                Tabs.Add(tab);
                placed.Add(group.Code);
            }
            foreach (var extra in byGroup.Keys.Where(k => !placed.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                var tab = new PermissionTabVM(extra, extra);
                foreach (var p in byGroup[extra])
                    tab.Items.Add(new PermissionItemVM(p.Id, p.Code, p.Feature, p.Operation, AppLanguage(p.Name, p.NameEn), p.EffectiveAllowed, p.Sensitive));
                Tabs.Add(tab);
            }
            Status = $"{permissions.Count} permissions loaded";
        }
        finally { IsBusy = false; }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var values = Tabs.SelectMany(t => t.Items).ToDictionary(x => x.PermissionId, x => x.IsAllowed);
            await PermAdminSvc.SaveForUserAsync(_userId, values, ct).ConfigureAwait(true);
            Status = "Permissions saved successfully.";
        }
        finally { IsBusy = false; }
    }

    // Legacy stored group_codes that belong to a catalog group
    // (pre-125 databases carried CRM on rows the catalog files under
    // CUSTOMERS, and PURCHASES before migration 125 renamed it).
    private static string StoredGroupAlias(string storedGroup) => storedGroup switch
    {
        "CRM" => "CUSTOMERS",
        "PURCHASES" => "PURCHASING",
        _ => storedGroup
    };

    private static string AppLanguage(string ar, string en)
        => (App.CurrentLanguageCode ?? "en").StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? ar : en;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PermissionTabVM
{
    public PermissionTabVM(string code, string name) { Code = code; Name = name; }
    public string Code { get; }
    public string Name { get; }
    public ObservableCollection<PermissionItemVM> Items { get; } = new();
    public int Count => Items.Count;
}

public sealed class PermissionItemVM : INotifyPropertyChanged
{
    private bool _isAllowed;
    public PermissionItemVM(long id, string code, string feature, string operation, string name, bool allowed, bool sensitive)
    { PermissionId=id; Code=code; Feature=feature; Operation=operation; Name=name; _isAllowed=allowed; IsSensitive=sensitive; }
    public long PermissionId { get; }
    public string Code { get; }
    public string Feature { get; }
    public string Operation { get; }
    public string Name { get; }
    public bool IsSensitive { get; }
    public bool IsAllowed { get => _isAllowed; set { if (_isAllowed == value) return; _isAllowed=value; PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(IsAllowed))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
