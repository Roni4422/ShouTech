using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Change Password window. There is NO ChangePassword API on IAuthSvc and no
/// password API at all in the src/ Application layer (UsrSvc only creates
/// users / toggles active), so this window composes the existing app-layer
/// primitives exactly like AuthServ does: PwdPolicy.ValidateConfigured
/// (Phase 4 policy: min 12 + upper/lower/digit/symbol + deny-list),
/// PwdHash.Verify for the current password and PwdHash.Create for the new
/// one. It covers both credential stores AuthServ authenticates against:
/// the database users table first, then the local company-administrator
/// record (CoLocalStore). The change is audited (security_audit_log via
/// SecurityAuditLog + AppHost.Audit). Passwords are never logged.
/// </summary>
public sealed class ChangePasswordWin : BaseWin
{
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly PasswordBox _current = new();
    private readonly PasswordBox _new = new();
    private readonly PasswordBox _confirm = new();
    private readonly TextBlock _userInfo = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _changeButton = new();
    private readonly Button _closeButton = new();

    public ChangePasswordWin()
    {
        Width = 520;
        Height = 430;
        MinWidth = 460;
        MinHeight = 380;
        BuildUi();
        Loaded += OnLoaded;
    }

    protected override bool PersistGeometry => false;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Auth.IsAuthenticated && string.IsNullOrWhiteSpace(CurrentUserId))
        {
            MessageBox.Show(this, T("ChangePassword.NotSignedIn"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        _userInfo.Text = $"{CurrentUserDisplay} ({CurrentUserId})";
        _current.Focus();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_changeButton, "ChangePassword.Change");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _changeButton.Click += async (_, _) => await ChangeAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_changeButton, _closeButton));

        // One-pair form grid: side labels, fields stretch, edges aligned
        // down the form (Phenix rule).
        var form = F2Ui.CreateFormGrid(1);
        _userInfo.FontWeight = FontWeights.SemiBold;
        _userInfo.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(_userInfo, 0);
        Grid.SetColumnSpan(_userInfo, 2);
        form.Children.Add(_userInfo);
        F2Ui.AddField(_labels, form, 1, 0, "ChangePassword.Current", _current);
        F2Ui.AddField(_labels, form, 2, 0, "ChangePassword.New", _new);
        F2Ui.AddField(_labels, form, 3, 0, "ChangePassword.Confirm", _confirm);
        root.Children.Add(form);

        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("ChangePassword.Title");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var b in new[] { _changeButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
    }

    private async Task ChangeAsync()
    {
        try
        {
            var username = CurrentUserId;
            var current = _current.Password;
            var next = _new.Password;
            var confirm = _confirm.Password;

            if (string.IsNullOrEmpty(current))
                throw new InvalidOperationException(T("ChangePassword.CurrentRequired"));
            if (next != confirm)
                throw new InvalidOperationException(T("ChangePassword.Mismatch"));

            var errors = PwdPolicy.ValidateConfigured(next, username);
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            if (string.Equals(next, current, StringComparison.Ordinal))
                throw new InvalidOperationException(T("ChangePassword.SameAsCurrent"));

            _changeButton.IsEnabled = false;
            try
            {
                var changed = await ChangeDatabasePasswordAsync(username, current, next).ConfigureAwait(true);
                if (!changed)
                    changed = await ChangeLocalAdminPasswordAsync(username, current, next).ConfigureAwait(true);
                if (!changed)
                    throw new InvalidOperationException(T("ChangePassword.CurrentIncorrect"));
            }
            finally
            {
                _changeButton.IsEnabled = true;
            }

            await SecurityAuditLog.WriteAsync(CurrentCompanyCode, null, username, "PASSWORD_CHANGED", "Password changed by the user.");
            if (AppHost.Audit is { } audit)
                await audit.LogUserActionAsync(username, "PasswordChanged", "User changed their own password.");
            _statusBar.Text = T("ChangePassword.Changed");
            MessageBox.Show(this, T("ChangePassword.Changed"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Database users path (mirrors AuthServ's credential lookup).</summary>
    private async Task<bool> ChangeDatabasePasswordAsync(string username, string current, string next)
    {
        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;
        var isPg = DatabaseRuntime.IsPostgreSql();
        await using var connection = isPg
            ? (DbConnection)new NpgsqlConnection(connectionString)
            : new SqliteConnection(connectionString);
        await connection.OpenAsync();

        string? userId = null, hash = null, salt = null;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = isPg
                ? """
                  SELECT u.user_id, u.password_hash, u.password_salt FROM users u
                  INNER JOIN companies c ON c.company_id = u.company_id
                  WHERE c.company_code = @code AND u.username = @user
                    AND u.is_active = TRUE AND u.is_deleted = FALSE LIMIT 1
                  """
                : """
                  SELECT u.user_id, u.password_hash, u.password_salt FROM users u
                  INNER JOIN companies c ON c.company_id = u.company_id
                  WHERE c.company_code = @code AND u.username = @user
                    AND u.is_active = 1 AND u.is_deleted = 0 LIMIT 1
                  """;
            Add(cmd, "@code", CurrentCompanyCode);
            Add(cmd, "@user", username);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return false;
            userId = reader["user_id"]?.ToString();
            hash = reader["password_hash"]?.ToString();
            salt = reader["password_salt"]?.ToString();
        }

        if (string.IsNullOrWhiteSpace(userId) || !PwdHash.Verify(current, hash ?? string.Empty, salt ?? string.Empty))
            return false;

        var created = PwdHash.Create(next);
        await using var update = connection.CreateCommand();
        update.CommandText = isPg
            ? "UPDATE users SET password_hash = @hash, password_salt = @salt, failed_login_count = 0, locked_until = NULL WHERE user_id = @id::uuid"
            : "UPDATE users SET password_hash = @hash, password_salt = @salt, failed_login_count = 0, locked_until = NULL WHERE user_id = @id";
        // SQLite user_id is TEXT; PostgreSQL user_id may be uuid or text — the
        // @id::uuid cast only applies when the stored id parses as a Guid;
        // fall back to a plain text comparison otherwise.
        if (isPg && !Guid.TryParse(userId, out _))
            update.CommandText = "UPDATE users SET password_hash = @hash, password_salt = @salt, failed_login_count = 0, locked_until = NULL WHERE CAST(user_id AS TEXT) = @id";
        Add(update, "@hash", created.Hash);
        Add(update, "@salt", created.Salt);
        Add(update, "@id", userId);
        return await update.ExecuteNonQueryAsync() == 1;
    }

    /// <summary>Local company-administrator path (CoLocalStore record).</summary>
    private async Task<bool> ChangeLocalAdminPasswordAsync(string username, string current, string next)
    {
        var company = await CoLocalStore.FindUserCompanyAsync(CurrentCompanyCode, username, current);
        if (company is null)
            return false;
        var created = PwdHash.Create(next);
        company.AdminPasswordHash = created.Hash;
        company.AdminPasswordSalt = created.Salt;
        company.AdminFailedLoginCount = 0;
        company.AdminLockedUntilUtc = null;
        await CoLocalStore.SaveCompanyAsync(company);
        return true;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
