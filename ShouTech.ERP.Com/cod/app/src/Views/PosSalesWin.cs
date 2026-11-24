using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Interaction;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// The session's sales (POS family P3, owner workstream
/// 2026-11-24): every document a POS session sold or returned —
/// sales_documents linked by pos_session_id (migration 066) —
/// with the tender split per bill, session totals, receipt
/// REPRINT through the real receipt path (InvRepo.Load +
/// InvPrn.PrintPosReceipt), and Return/Exchange opening
/// PosReturnWin on the chosen bill. This was the family's
/// missing surface: session sales were previously visible only
/// through aggregate reports.
/// </summary>
public sealed class PosSalesWin : BaseWin
{
    protected override string? ScheduleTintName => "Fallow";

    private sealed record SaleRow(
        long DocId, string DocNo, string Kind, string Time, string PartyName,
        decimal Total, decimal Cash, decimal Card, decimal OnAccount, string Status);

    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly ComboBox _session = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _totals = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _reprintButton = new();
    private readonly Button _returnButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private IReadOnlyList<PosSession> _sessions = new List<PosSession>();
    private List<SaleRow> _rows = new();

    public PosSalesWin()
    {
        Width = 980;
        Height = 620;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    /// <summary>PERM L5: the session-sales list is the DOC.POS.VIEW
    /// surface (generic fallback POS.REPORTS.VIEW); without it the
    /// window denies and closes, like the other guarded windows.</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.HasDoc(PermCat.Documents.PosView, PermCat.Pos.ReportsView))
        {
            AppHost.Errors.Report(ErrorCodes.PermissionDenied, null, "PosSalesWin.OnLoaded");
            MessageBox.Show(this, T("Permissions.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshSessionsAsync();
    }

    private PosSession? SelectedSession
        => _session.SelectedItem is PosSession s ? s : null;

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_reprintButton, "PosSales.Reprint");
        F2Ui.StyleButton(_returnButton, "PosSales.Return");
        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _reprintButton.Click += (_, _) => ReprintSelected();
        _returnButton.Click += (_, _) => ReturnSelected();
        _refreshButton.Click += async (_, _) => await RefreshSessionsAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_reprintButton, _returnButton, _refreshButton, _closeButton));

        var content = new DockPanel();

        var form = F2Ui.CreateFormGrid(3);
        form.Margin = new Thickness(0, 0, 0, 4);
        F2Ui.AddField(_labels, form, 0, 0, "PosSales.Session", _session);
        DockPanel.SetDock(form, Dock.Top);
        content.Children.Add(form);
        _session.SelectionChanged += async (_, _) => await RefreshSalesAsync();

        _totals.Margin = new Thickness(0, 6, 0, 0);
        _totals.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(_totals, Dock.Bottom);
        content.Children.Add(_totals);

        F2Ui.ConfigureGrid(_grid);
        DataGridTableBehavior.SetShowRowNumbers(_grid, true);
        _grid.IsReadOnly = true;
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSales.DocNo", nameof(SaleRow.DocNo), 110));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSales.Kind", nameof(SaleRow.Kind), 70, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSales.Time", nameof(SaleRow.Time), 130));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSales.Customer", nameof(SaleRow.PartyName), 180));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSales.Total", nameof(SaleRow.Total), 100, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Cash", nameof(SaleRow.Cash), 90, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Card", nameof(SaleRow.Card), 90, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSales.OnAccount", nameof(SaleRow.OnAccount), 90, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSales.Status", nameof(SaleRow.Status), 90, center: true));
        _grid.MouseDoubleClick += (_, _) => ReprintSelected();
        content.Children.Add(_grid);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("PosSales.Title");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _reprintButton, _returnButton, _refreshButton, _closeButton }, T);
    }

    private async Task RefreshSessionsAsync()
    {
        try
        {
            var service = AppHost.Pos;
            if (service is null) { _statusBar.Text = T("POS.ServiceUnavailable"); return; }
            _sessions = await service.GetSessionsAsync(CurrentCompanyCode);
            var selectedId = SelectedSession?.Id;
            _session.ItemsSource = _sessions;
            _session.DisplayMemberPath = nameof(PosSession.DisplayName);
            _session.SelectedItem =
                _sessions.FirstOrDefault(s => s.Id == selectedId)
                ?? _sessions.FirstOrDefault(s => s.Status == PosSessionStatuses.Open)
                ?? (_sessions.Count > 0 ? _sessions[0] : null);
            if (_session.SelectedItem is null)
                _statusBar.Text = T("PosSales.NoSession");
            await RefreshSalesAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosSalesWin.RefreshSessionsAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshSalesAsync()
    {
        try
        {
            var session = SelectedSession;
            _rows = session is null ? new List<SaleRow>() : LoadSales(session.Id);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _rows;

            var sales = _rows.Where(r => r.Kind == "POS").Sum(r => r.Total);
            var returns = _rows.Where(r => r.Kind == "SRT").Sum(r => r.Total);
            _totals.Text = _rows.Count == 0
                ? string.Empty
                : $"{T("PosSales.SalesTotal")}: {F2Ui.Fmt(sales)} · {T("PosSales.ReturnsTotal")}: {F2Ui.Fmt(returns)} · {T("PosSales.NetTotal")}: {F2Ui.Fmt(sales - returns)}";
            _statusBar.Text = _rows.Count.ToString(CultureInfo.CurrentCulture);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosSalesWin.RefreshSalesAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await Task.CompletedTask;
    }

    private void ReprintSelected()
    {
        try
        {
            if (_grid.SelectedItem is not SaleRow row)
            {
                MessageBox.Show(this, T("PosSales.SelectSale"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var session = SelectedSession;
            var kind = row.Kind == "SRT" ? InvDocKind.SalesReturn : InvDocKind.PosSale;
            var doc = InvRepo.Load(row.DocId, kind);
            if (doc is null)
                throw new InvalidOperationException(row.DocNo);
            var tenders = LoadTenders(row);
            var change = tenders.Sum(t => t.ChangeAmount);
            InvPrn.PrintPosReceipt(doc, tenders, change, CurrentCompanyDisplay, session?.SessionNo, session?.Cashier);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosSalesWin.ReprintSelected");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReturnSelected()
    {
        if (_grid.SelectedItem is not SaleRow row || row.Kind != "POS")
        {
            MessageBox.Show(this, T("PosSales.SelectSale"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var session = SelectedSession;
        var win = new PosReturnWin(session?.Id, row.DocNo) { Owner = this };
        win.ShowDialog();
        _ = RefreshSalesAsync();
    }

    // ----------------------------------------------------------------
    // Read model (single consumer): the session's documents with
    // their tender split, and one document's tenders for reprint.
    // ----------------------------------------------------------------

    private static List<SaleRow> LoadSales(Guid sessionId)
    {
        var company = AppState.CurrentCompanyCode ?? string.Empty;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.doc_id, d.doc_no, d.doc_kind, d.created_at,
                   COALESCE(d.party_name, ''), d.grand_total, d.status,
                   COALESCE(SUM(CASE WHEN t.tender_type = 'Cash' THEN t.amount END), 0),
                   COALESCE(SUM(CASE WHEN t.tender_type = 'Card' THEN t.amount END), 0),
                   COALESCE(SUM(CASE WHEN t.tender_type = 'OnAccount' THEN t.amount END), 0)
            FROM sales_documents d
            LEFT JOIN pos_sale_tenders t
                   ON t.company_id = d.company_id AND t.doc_id = d.doc_id
                  AND t.doc_kind = d.doc_kind AND COALESCE(t.is_deleted, 0) = 0
            WHERE d.company_id = @company
              AND d.pos_session_id = @session
              AND COALESCE(d.is_deleted, 0) = 0
            GROUP BY d.doc_id, d.doc_no, d.doc_kind, d.created_at, d.party_name, d.grand_total, d.status
            ORDER BY d.created_at, d.doc_id;
            """;
        Add(command, "@company", company);
        Add(command, "@session", sessionId.ToString("D"));

        var result = new List<SaleRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SaleRow(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                reader.GetString(6)));
        }
        return result;
    }

    private static List<PosTender> LoadTenders(SaleRow row)
    {
        var company = AppState.CurrentCompanyCode ?? string.Empty;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tender_type, amount, tendered_amount, change_amount, card_ref
            FROM pos_sale_tenders
            WHERE company_id = @company AND doc_id = @doc AND doc_kind = @kind
              AND COALESCE(is_deleted, 0) = 0
            ORDER BY created_at;
            """;
        Add(command, "@company", company);
        Add(command, "@doc", row.DocId);
        Add(command, "@kind", row.Kind);

        var result = new List<PosTender>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PosTender
            {
                DocId = row.DocId,
                DocNo = row.DocNo,
                DocKind = row.Kind,
                TenderType = reader.GetString(0),
                Amount = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
                TenderedAmount = reader.IsDBNull(2) ? null : Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                ChangeAmount = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                CardRef = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }
        return result;
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
