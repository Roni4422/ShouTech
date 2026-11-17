// ========================================================================
// FILE: Services/DataExchangeGuard.cs
// PROJECT: SHOUTECH ERP — Data-exchange import governance (Fix Phase Step 10)
//
// One service-layer gate for EVERY import path of the data-exchange
// surface (STEI full package, STEI dataset package, Excel workbook,
// .shx container):
//
//   * Permission: an import runs only when the signed-in session holds
//     DATA.IMPORT (PermCat.Data.Import). Before Step 10 the code existed
//     in the permission catalog but was referenced by no code, so any
//     authenticated user who could open DatWin could import — and
//     overwrite — data (sweep DX-03). The check lives HERE, in the
//     service layer, so no caller (window, backup service, future API)
//     can bypass it by opening a different surface. Backup RESTORE does
//     not pass through this gate: it is governed by BACKUP.RESTORE in
//     BkpServ, which calls the engine's restore entry point instead.
//
//   * Denial: a refused import throws a catalog-coded AppError
//     (DATA-4004), is reported through AppHost.Errors, and is written to
//     the durable security audit log as DATA_IMPORT_DENIED.
//
//   * Audit: every completed import (success or partial/rolled-back) is
//     written to the security audit log as DATA_IMPORT with the package
//     or dataset identity and the honest row counts (sweep DX-06:
//     previously no import of any kind left an audit row).
//
//   * Legacy tables: packages produced before migrations 059/061/063
//     can carry tables the current schema dropped (accounts, customers,
//     suppliers, bank_accounts). Those tables must never be recreated
//     by an import; the STEI engine skips them and names them in the
//     import audit details (Step 2 flag F-3).
// ========================================================================

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ShouTech.Domain;

namespace ShouTech.App.Services;

/// <summary>Import governance shared by all data-exchange import paths.</summary>
public static class DataExchangeGuard
{
    /// <summary>
    /// Tables dropped by the unification migrations (059 accounts,
    /// 061 customers/suppliers, 063 bank_accounts — dead since Step 8 and
    /// removed from every writer/catalog). A pre-unification package may
    /// still carry them; import skips them instead of recreating orphan
    /// legacy tables or failing the whole package.
    /// </summary>
    public static readonly IReadOnlySet<string> LegacyDroppedTables =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "accounts",
            "customers",
            "suppliers",
            "bank_accounts"
        };

    public static bool IsLegacyDroppedTable(string tableName)
        => !string.IsNullOrWhiteSpace(tableName) && LegacyDroppedTables.Contains(tableName.Trim());

    /// <summary>
    /// The DATA.IMPORT gate. Throws <see cref="AppError"/> DATA-4004 when
    /// the current session lacks the permission; the denial is reported
    /// and audit-logged before the throw, and nothing has been written
    /// to the database at that point.
    /// </summary>
    /// <param name="operation">Short path identifier for the audit details (e.g. "STEI full package").</param>
    public static void RequireImportPermission(string operation)
    {
        if (AppHost.Permissions.Has(PermCat.Data.Import))
            return;

        var user = CurrentUserName();
        var company = AppState.CurrentCompanyCode ?? string.Empty;
        AppHost.Errors.Report(
            ErrorCodes.ImportPermissionDenied, null,
            $"DataExchangeGuard.RequireImportPermission | operation={operation} | user={user}");
        _ = SecurityAuditLog.WriteAsync(
            company, AppState.CurrentUser?.Id, user,
            SecurityAuditLog.DataImportDenied,
            $"operation={operation} | user lacks DATA.IMPORT");
        throw new AppError(
            ErrorCodes.ImportPermissionDenied,
            "You do not have permission to import data (DATA.IMPORT).");
    }

    /// <summary>
    /// Durable audit row for a completed import attempt. Best-effort by
    /// design (SecurityAuditLog never throws into the caller), awaited so
    /// the row is durable before the caller reports success.
    /// </summary>
    public static async Task WriteImportAuditAsync(string details)
    {
        await SecurityAuditLog.WriteAsync(
            AppState.CurrentCompanyCode ?? string.Empty,
            AppState.CurrentUser?.Id,
            CurrentUserName(),
            SecurityAuditLog.DataImport,
            details);
    }

    internal static string CurrentUserName()
    {
        var user = AppState.CurrentUser;
        if (user is not null)
            return !string.IsNullOrWhiteSpace(user.Username) ? user.Username : user.FullName;
        return Environment.UserName;
    }
}
