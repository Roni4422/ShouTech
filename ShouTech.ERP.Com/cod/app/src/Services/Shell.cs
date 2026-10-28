// ========================================================================
// FILE: Services/Shell.cs
// PROJECT: SHOUTECH ERP — Navigation, module menu, user prefs service
// Merged: NavServ, ModMenuServ, UsrPrefSvc
// Theme/ShlChrHlpr not present in this tree version
// ========================================================================

#nullable enable

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System;
using ShouTech.App.Configuration;
using ShouTech.App.Navigation;
using ShouTech.App.ViewModels;
using ShouTech.App.Views.Modules;

namespace ShouTech.App.Services
{

    // ========== NavServ.cs ==========
/// <summary>
    /// Central navigation service — resolves ribbon actions to module workspace views.
    /// </summary>
    public sealed class NavServ : INavServ
    {
        private readonly ILogServ _logging;
        private readonly ConcurrentDictionary<string, object> _cache = new();

        public NavServ(ILogServ logging)
        {
            _logging = logging;
        }

        public object NavigateTo(string pageKey)
        {
            if (string.Equals(pageKey, "dashboard", StringComparison.OrdinalIgnoreCase))
                return GetOrCreate("dashboard", () => new DashVw());

            return GetOrCreate(pageKey, () => CreateWorkspace(pageKey, "General", pageKey));
        }

        public Task<object> NavigateToAsync(string pageKey) =>
            Task.FromResult(NavigateTo(pageKey));

        public object NavigateToModule(string actionTitle, string moduleKey, string? tabName = null, string? tabHeader = null)
        {
            var module = string.IsNullOrWhiteSpace(moduleKey)
                ? RibModMap.ResolveModule(tabName, tabHeader)
                : moduleKey;

            var cacheKey = $"{module}::{actionTitle}";
            return GetOrCreate(cacheKey, () => CreateWorkspace(actionTitle, module, actionTitle));
        }

        public void ClearCache() => _cache.Clear();

        private object GetOrCreate(string key, Func<object> factory)
        {
            return _cache.GetOrAdd(key, _ =>
            {
                _logging.LogInfo("Navigation", $"Opened: {key}");
                return factory();
            });
        }

        private static ModuleWorkspaceView CreateWorkspace(string title, string moduleKey, string actionTitle)
        {
            var vm = new ModWsVM(title, moduleKey, actionTitle);
            return new ModuleWorkspaceView { DataContext = vm };
        }
    }

    // ========== ModMenuServ.cs ==========
/// <summary>
    /// Builds the list of modules that should appear in the main ribbon/menu
    /// according to the current license (same behavior as local market leaders).
    /// </summary>
    public sealed class ModMenuServ
    {
        private readonly ILicServ _lic;

        public ModMenuServ(ILicServ lic)
        {
            _lic = lic ?? throw new ArgumentNullException(nameof(lic));
        }

        public async Task<IReadOnlyList<string>> GetVisibleModulesAsync(CancellationToken ct = default)
        {
            var result = await _lic.ValidateAsync(ct);
            var list = new List<string>();

            if (!result.IsUsable)
                return list;

            // Core modules – always shown for Core / Demo
            if (result.Edition is LicEd.Core or LicEd.Demo)
            {
                list.Add(ModKey.Acc);
                list.Add(ModKey.Inv);
                list.Add(ModKey.Sales);
                list.Add(ModKey.Purch);
            }

            // Optional modules – only if licensed
            void AddIf(string key)
            {
                if (_lic.IsModuleEnabled(key))
                    list.Add(key);
            }

            AddIf(ModKey.Hr);
            AddIf(ModKey.Mfg);
            AddIf(ModKey.Pos);
            AddIf(ModKey.Ai);
            AddIf(ModKey.AdvRpt);
            AddIf(ModKey.MultiCur);
            AddIf(ModKey.WhiteLabel);

            return list;
        }
    }

    // ========== UsrPrefSvc.cs ==========
public interface IUsrPrefSvc
    {
        UsrPref Load();
        void Save(UsrPref prefs);
        void ApplyToWindow(Window window);
        void SaveFromWindow(Window window);
    }

    public class UsrPrefSvc : IUsrPrefSvc
    {
        private UsrPref _prefs;

        public UsrPrefSvc()
        {
            _prefs = UsrPref.Load();
        }

        public UsrPref Load() => _prefs;

        public void Save(UsrPref prefs)
        {
            _prefs = prefs;
            _prefs.Save();
        }

        public void ApplyToWindow(Window window) => _prefs.ApplyToWindow(window);

        public void SaveFromWindow(Window window) => _prefs.SaveFromWindow(window);
    }

}
