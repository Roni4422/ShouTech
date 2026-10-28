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
            foreach (var group in groups)
            {
                var tab = new PermissionTabVM(group.Code, AppLanguage(group.Name, group.NameEn));
                foreach (var p in permissions.Where(x => string.Equals(x.Code.Split('.')[0], PrefixForGroup(group.Code), StringComparison.OrdinalIgnoreCase)))
                    tab.Items.Add(new PermissionItemVM(p.Id, p.Code, p.Feature, p.Operation, AppLanguage(p.Name, p.NameEn), p.EffectiveAllowed, p.Sensitive));
                if (tab.Items.Count > 0) Tabs.Add(tab);
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

    private static string PrefixForGroup(string group) => group switch
    {
        "SYSTEM" => "SYS", "COMPANY" => "ORG", "INVENTORY" => "INV", "ACCOUNTING" => "ACC", "VOUCHERS" => "VCH",
        "SALES" => "SALES", "PURCHASING" => "PURCH", "POS" => "POS", "CUSTOMERS" => "CRM", "REPRESENTATIVES" => "REP",
        "MANUFACTURING" => "MFG", "BANKING" => "BANK", "DELIVERY" => "DELIVERY", "REPORTING" => "REPORT", "DATA" => "DATA",
        "BACKUP" => "BACKUP", "AUDIT" => "AUDIT", "LICENSING" => "LIC", "UPDATES" => "UPDATE", "AI" => "AI", "HELP" => "HELP", _ => group
    };

    private static string AppLanguage(string ar, string en)
        => (App.CurrentLanguageCode ?? "ar").StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? ar : en;

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
