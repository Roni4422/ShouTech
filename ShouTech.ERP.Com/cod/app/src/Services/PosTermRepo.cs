#nullable enable

using System;
using System.Collections.Generic;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

using ShouTech.Domain;
namespace ShouTech.App.Services;

/// <summary>
/// POS terminal definitions (pos_terminals, migrations 036 + 119)
/// — the data layer of the dedicated terminal definition window
/// (PosTermWin). A terminal binds a warehouse, the cash and
/// card/bank GL accounts, the drawer and receipt printer, the
/// walk-in default customer, the price level the till sells
/// from, and the users permitted to open sessions on it.
/// Company scoping follows the POS tables' convention: the
/// company CODE (as PosRepository reads them).
/// </summary>
public static class PosTermRepo
{
    public sealed record PosTermRec(
        string Id,
        string Code,
        string NameEn,
        string NameAr,
        string? WarehouseCode,
        string? CashAccountCode,
        string? BankAccountCode,
        string? DrawerName,
        string? PrinterName,
        string? DefaultCustomerCode,
        string? PriceLevel,
        string? PermittedUsers,
        bool IsActive);

    public static List<PosTermRec> LoadAll()
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, code, name_en, COALESCE(name_ar, ''), warehouse_code,
                   cash_account_code, bank_account_code, drawer_name, printer_name,
                   default_customer_code, price_level, permitted_users,
                   COALESCE(is_active, 1)
            FROM pos_terminals
            WHERE company_id = @company
              AND COALESCE(is_deleted, 0) = 0
            ORDER BY code;
            """;
        Add(command, "@company", company);

        var result = new List<PosTermRec>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(Read(reader));
        return result;
    }

    public static PosTermRec? FindByCode(string code)
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, code, name_en, COALESCE(name_ar, ''), warehouse_code,
                   cash_account_code, bank_account_code, drawer_name, printer_name,
                   default_customer_code, price_level, permitted_users,
                   COALESCE(is_active, 1)
            FROM pos_terminals
            WHERE company_id = @company
              AND code = @code
              AND COALESCE(is_deleted, 0) = 0
            LIMIT 1;
            """;
        Add(command, "@company", company);
        Add(command, "@code", code.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>Insert (Id empty) or update a terminal definition.
    /// Returns the row id. Codes are unique per company among
    /// non-deleted rows (ux_pos_terminals_company_code).</summary>
    public static string Save(PosTermRec value, string user)
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(value.Id))
        {
            var id = Guid.NewGuid().ToString("D");
            command.CommandText = """
                INSERT INTO pos_terminals
                    (id, company_id, code, name_en, name_ar, warehouse_code,
                     cash_account_code, bank_account_code, drawer_name, printer_name,
                     default_customer_code, price_level, permitted_users,
                     is_active, is_deleted, created_by, created_at, updated_by, updated_at)
                VALUES
                    (@id, @company, @code, @nameEn, @nameAr, @warehouse,
                     @cashAccount, @bankAccount, @drawer, @printer,
                     @customer, @priceLevel, @users,
                     @active, 0, @user, CURRENT_TIMESTAMP, @user, CURRENT_TIMESTAMP);
                """;
            Add(command, "@id", id);
            Bind(command, value, company, user);
            command.ExecuteNonQuery();
            return id;
        }

        command.CommandText = """
            UPDATE pos_terminals SET
                code = @code, name_en = @nameEn, name_ar = @nameAr, warehouse_code = @warehouse,
                cash_account_code = @cashAccount, bank_account_code = @bankAccount,
                drawer_name = @drawer, printer_name = @printer,
                default_customer_code = @customer, price_level = @priceLevel,
                permitted_users = @users, is_active = @active,
                updated_by = @user, updated_at = CURRENT_TIMESTAMP
            WHERE company_id = @company AND id = @id AND COALESCE(is_deleted, 0) = 0;
            """;
        Add(command, "@id", value.Id);
        Bind(command, value, company, user);
        command.ExecuteNonQuery();
        return value.Id;
    }

    public static void SoftDelete(string id, string user)
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pos_terminals SET is_deleted = 1, updated_by = @user, updated_at = CURRENT_TIMESTAMP
            WHERE company_id = @company AND id = @id AND COALESCE(is_deleted, 0) = 0;
            """;
        Add(command, "@company", company);
        Add(command, "@id", id);
        Add(command, "@user", user);
        command.ExecuteNonQuery();
    }

    /// <summary>Distinct price levels defined on materials — the
    /// terminal's price-level choices.</summary>
    public static List<string> LoadPriceLevels()
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT price_level
            FROM material_price_list
            WHERE company_id = @company
              AND price_level IS NOT NULL AND price_level <> ''
            ORDER BY price_level;
            """;
        Add(command, "@company", company);
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>True when the terminal restricts session opening
    /// to a permitted-users list and the given user is not on it.
    /// An empty list means every user may open (permission
    /// POS.SESSION.OPEN still applies at the window).</summary>
    public static bool IsUserPermitted(PosTermRec terminal, string username)
    {
        if (string.IsNullOrWhiteSpace(terminal.PermittedUsers))
            return true;
        foreach (var entry in terminal.PermittedUsers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(entry, username, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static PosTermRec Read(DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            Convert.ToBoolean(reader.GetValue(12), System.Globalization.CultureInfo.InvariantCulture));

    private static void Bind(DbCommand command, PosTermRec value, string company, string user)
    {
        Add(command, "@company", company);
        Add(command, "@code", value.Code.Trim());
        Add(command, "@nameEn", value.NameEn.Trim());
        Add(command, "@nameAr", string.IsNullOrWhiteSpace(value.NameAr) ? null : value.NameAr.Trim());
        Add(command, "@warehouse", NullIfBlank(value.WarehouseCode));
        Add(command, "@cashAccount", NullIfBlank(value.CashAccountCode));
        Add(command, "@bankAccount", NullIfBlank(value.BankAccountCode));
        Add(command, "@drawer", NullIfBlank(value.DrawerName));
        Add(command, "@printer", NullIfBlank(value.PrinterName));
        Add(command, "@customer", NullIfBlank(value.DefaultCustomerCode));
        Add(command, "@priceLevel", NullIfBlank(value.PriceLevel));
        Add(command, "@users", NullIfBlank(value.PermittedUsers));
        Add(command, "@active", value.IsActive ? 1 : 0);
        Add(command, "@user", user);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string RequireCompany()
    {
        var company = AppState.CurrentCompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(company))
            throw new InvalidOperationException("Select a company before managing POS terminals.");
        return company;
    }

    private static DbConnection OpenConnection()
    {
        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The active company database connection is not configured.");

        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
        connection.Open();
        return connection;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
