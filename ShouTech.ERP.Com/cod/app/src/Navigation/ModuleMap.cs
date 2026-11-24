// ========================================================================
// FILE: Navigation/ModuleMap.cs
// PROJECT: SHOUTECH ERP — Ribbon key → license module map
//
// The complete-by-construction mapping of the dongle/module
// licensing design §2.3, updated for the key set that exists today
// (CRM.* and HR.* keys have since been built; they take their prefix
// defaults exactly as the design's future-keys rule prescribed).
//
// Rule: every nav key resolves by (1) the exception table, else
// (2) its prefix default. A Kern.Tests completeness test asserts
// every LocProps.Key in MWin.xaml and every router case key
// resolves here, so no key can ship unmapped.
// ========================================================================

#nullable enable

using System;
using System.Collections.Generic;
using ShouTech.Domain.Enums;

namespace ShouTech.App.Navigation
{
    public static class ModuleMap
    {
        /// <summary>The base bundle (ACC/INV/SALES/PURCH) plus the
        /// platform surface — entitled with any usable license and
        /// never module-gated (design §2.2).</summary>
        public const string Core = "CORE";

        /// <summary>File/session/help surface that must work even
        /// with NO license at all — the path to fix a license can
        /// never itself require one (design §2.2).</summary>
        public const string Platform = "PLATFORM";

        private static readonly Dictionary<string, string> Exceptions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Reports.CustomReports"] = ModKey.AdvRpt,
                ["Reports.CustomizeReports"] = ModKey.AdvRpt,
                ["Tools.AI"] = ModKey.Ai,
                ["Invoices.CloseCash"] = ModKey.Pos,
                ["View.ManufacturingTree"] = ModKey.Mfg,
                ["MfgOrder.Title"] = ModKey.Mfg
            };

        private static readonly Dictionary<string, string> PrefixDefaults =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["POS"] = ModKey.Pos,
                ["Manufacturing"] = ModKey.Mfg,
                ["CRM"] = ModKey.Crm,
                ["HR"] = ModKey.Hr,
                ["Help"] = Platform,
                ["Exit"] = Platform,
                ["Menu"] = Platform
            };

        /// <summary>The add-on modules a license payload can grant or
        /// withhold. Everything else (CORE, PLATFORM) is not gated.</summary>
        private static readonly HashSet<string> GatedModules = new(StringComparer.OrdinalIgnoreCase)
        {
            ModKey.Pos, ModKey.Mfg, ModKey.Crm, ModKey.Hr, ModKey.Ai, ModKey.AdvRpt
        };

        public static bool IsGatedModule(string? moduleKey)
            => moduleKey is not null && GatedModules.Contains(moduleKey);

        /// <summary>Resolves a ribbon/router action key to its
        /// license module (a ModKey value, CORE, or PLATFORM).</summary>
        public static string ResolveModuleForKey(string? actionKey)
        {
            if (string.IsNullOrWhiteSpace(actionKey))
                return Core;

            var key = actionKey.Trim();
            if (Exceptions.TryGetValue(key, out var exception))
                return exception;

            var dot = key.IndexOf('.');
            var prefix = dot > 0 ? key[..dot] : key;
            if (PrefixDefaults.TryGetValue(prefix, out var byPrefix))
                return byPrefix;

            return Core;
        }

        /// <summary>Whole-tab license module for the shell's tab
        /// surfacing (mixed tabs resolve to CORE; their gated buttons
        /// resolve per key).</summary>
        public static string ResolveModuleForTab(string? tabName) => tabName?.Trim() switch
        {
            "TabPOS" => ModKey.Pos,
            "TabManufacturing" => ModKey.Mfg,
            "TabHR" => ModKey.Hr,
            _ => Core
        };

        /// <summary>Every module the license vocabulary knows, for
        /// the add-ons surface: the core bundle plus each add-on.</summary>
        public static IReadOnlyList<(string ModuleKey, bool IsCore)> AllModules { get; } = new[]
        {
            (Core, true),
            (ModKey.Pos, false),
            (ModKey.Mfg, false),
            (ModKey.Crm, false),
            (ModKey.Hr, false),
            (ModKey.Ai, false),
            (ModKey.AdvRpt, false)
        };
    }
}
